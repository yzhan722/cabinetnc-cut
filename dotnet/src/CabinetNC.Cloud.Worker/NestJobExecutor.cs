using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Storage;
using CabinetNC.Compute.Core.Nesting;
using Microsoft.Extensions.Logging;

namespace CabinetNC.Cloud.Worker;

/// <summary>
/// One iteration of the worker loop: claim → verify input → run the shared runner → store result →
/// fenced completion. Every failure is classified as retryable (transient I/O, unexpected exception)
/// or final (bad input, engine rejection); the repository applies the attempt budget. Error messages
/// stored for the operator never contain stack traces or exception text.
/// </summary>
public sealed class NestJobExecutor(
    IJobRepository jobs,
    IObjectStore objects,
    INestingRunner runner,
    CloudDbContext db,
    TimeProvider clock,
    WorkerOptions options,
    ILogger<NestJobExecutor> logger)
{
    /// <summary>Returns false when nothing was claimable, so the host can back off.</summary>
    public async Task<bool> ExecuteOneAsync(CancellationToken ct)
    {
        var lease = await jobs.TryClaimNextAsync(options.WorkerId, options.LeaseDuration, ct);
        if (lease is null)
            return false;

        var job = lease.Job;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["jobId"] = job.Id,
            ["correlationId"] = job.CorrelationId,
            ["workerId"] = options.WorkerId,
        });
        logger.LogInformation("Claimed job {JobId} (attempt {Attempt})", job.Id, job.AttemptCount);
        await AuditAsync(job, "job.claimed", ct);

        try
        {
            await AuditAsync(job, "job.started", ct);
            await RunAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Unhandled {ExceptionType} while executing job {JobId}", ex.GetType().FullName, job.Id);
            try
            {
                await FailAsync(job, ApiErrorCodes.ComputeFailed, $"Unexpected {ex.GetType().Name} in the worker.", retryable: true, ct);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                // Nothing more we can do here; the lease expiry makes the job reclaimable.
                logger.LogError("Could not record the failure of job {JobId}: {ExceptionType}", job.Id, inner.GetType().FullName);
            }
        }

        return true;
    }

    async Task RunAsync(ComputeJobEntity job, CancellationToken ct)
    {
        if (!string.Equals(job.JobType, JobTypes.Nest, StringComparison.Ordinal))
        {
            await FailAsync(job, ApiErrorCodes.InvalidRequest, $"Unsupported job type '{job.JobType}'.", retryable: false, ct);
            return;
        }

        byte[] inputBytes;
        try
        {
            inputBytes = await ReadAllAsync(job.InputObjectKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectStoreNotFoundException)
        {
            await FailAsync(job, ApiErrorCodes.StorageFailed, "The job input object is missing.", retryable: false, ct);
            return;
        }
        catch (Exception ex)
        {
            await FailAsync(job, ApiErrorCodes.StorageFailed, $"The job input could not be read ({ex.GetType().Name}).", retryable: true, ct);
            return;
        }

        if (!string.Equals(Sha256(inputBytes), job.InputSha256, StringComparison.Ordinal))
        {
            await FailAsync(job, ApiErrorCodes.StorageFailed, "The job input object does not match its recorded SHA-256.", retryable: false, ct);
            return;
        }

        SubmitNestJobRequest request;
        try
        {
            request = CloudJson.Deserialize<SubmitNestJobRequest>(Encoding.UTF8.GetString(inputBytes));
        }
        catch (JsonException)
        {
            await FailAsync(job, ApiErrorCodes.InvalidRequest, "The job input is not a valid nest request.", retryable: false, ct);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        NestingOutput output;
        try
        {
            output = runner.Run(NestJobMapper.ToNestingInput(request));
        }
        catch (Exception ex)
        {
            await FailAsync(job, ApiErrorCodes.ComputeFailed, $"The nesting engine threw {ex.GetType().Name}.", retryable: true, ct);
            return;
        }
        stopwatch.Stop();

        if (!output.Ok)
        {
            // Deterministic rejection of this input; retrying the same bytes cannot succeed.
            await FailAsync(job, ApiErrorCodes.ComputeFailed, output.Error ?? "The nesting engine reported a failure.", retryable: false, ct);
            return;
        }

        var resultBytes = Encoding.UTF8.GetBytes(CloudJson.Serialize(NestJobMapper.ToPayload(output)));
        var resultKey = ObjectKeys.JobResult(job.TenantId, job.Id);
        try
        {
            await objects.PutAsync(resultKey, new MemoryStream(resultBytes, writable: false), "application/json", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(job, ApiErrorCodes.StorageFailed, $"The job result could not be stored ({ex.GetType().Name}).", retryable: true, ct);
            return;
        }

        var completion = new JobCompletion(resultKey, Sha256(resultBytes), EngineVersion.Current, stopwatch.ElapsedMilliseconds);
        if (!await jobs.MarkSucceededAsync(job.Id, options.WorkerId, completion, ct))
        {
            // Another worker reclaimed the job after our lease expired; its result is authoritative.
            logger.LogWarning("Lease for job {JobId} was lost before completion; result discarded", job.Id);
            return;
        }

        logger.LogInformation("Job {JobId} succeeded in {DurationMs} ms ({Engine})", job.Id, completion.DurationMs, output.Engine);
        await AuditAsync(job, "job.succeeded", ct, new { durationMs = completion.DurationMs, engineVersion = completion.EngineVersion, engine = output.Engine });
    }

    async Task FailAsync(ComputeJobEntity job, string errorCode, string message, bool retryable, CancellationToken ct)
    {
        var outcome = await jobs.MarkFailedAsync(job.Id, options.WorkerId, errorCode, message, retryable, ct);
        switch (outcome)
        {
            case JobStatus.Queued:
                logger.LogWarning("Job {JobId} attempt {Attempt} failed with {ErrorCode}; requeued", job.Id, job.AttemptCount, errorCode);
                await AuditAsync(job, "job.retry", ct, new { errorCode, attempt = job.AttemptCount });
                break;
            case JobStatus.Failed:
                logger.LogWarning("Job {JobId} failed permanently with {ErrorCode} after {Attempt} attempt(s)", job.Id, errorCode, job.AttemptCount);
                await AuditAsync(job, "job.failed", ct, new { errorCode, attempt = job.AttemptCount });
                break;
            default:
                logger.LogWarning("Lease for job {JobId} was lost before its failure could be recorded", job.Id);
                break;
        }
    }

    async Task AuditAsync(ComputeJobEntity job, string eventType, CancellationToken ct, object? details = null)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            TenantId = job.TenantId,
            UserId = job.UserId,
            DeviceId = job.DeviceId,
            JobId = job.Id,
            EventType = eventType,
            CorrelationId = job.CorrelationId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, CloudJson.Options),
            CreatedAtUtc = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    async Task<byte[]> ReadAllAsync(string key, CancellationToken ct)
    {
        await using var stream = await objects.OpenReadAsync(key, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
