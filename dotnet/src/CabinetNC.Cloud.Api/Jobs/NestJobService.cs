using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace CabinetNC.Cloud.Api.Jobs;

public sealed class NestJobService(
    CloudDbContext db,
    IJobRepository jobs,
    IObjectStore objects,
    TimeProvider clock)
{
    public Task<SubmitNestJobResponse> SubmitAsync(
        SubmitNestJobRequest request,
        string idempotencyKey,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        NestJobValidator.Validate(request);
        return SubmitCoreAsync(JobTypes.Nest, Encoding.UTF8.GetBytes(CloudJson.Serialize(request)), idempotencyKey, principal, correlationId, ct);
    }

    /// <summary>True-shape contract; the canonical request bytes are what the worker hashes and executes.</summary>
    public Task<SubmitNestJobResponse> SubmitV2Async(
        SubmitNestJobRequestV2 request,
        string idempotencyKey,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        if (NestRequestV2Rules.Validate(request) is { } violation)
            Invalid(violation);
        return SubmitCoreAsync(JobTypes.NestV2, Encoding.UTF8.GetBytes(CloudJson.Serialize(request)), idempotencyKey, principal, correlationId, ct);
    }

    public Task<SubmitNestJobResponse> SubmitOperationsAsync(
        SubmitOperationsJobRequest request,
        string idempotencyKey,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        if (CamRequestRules.Validate(request) is { } violation)
            Invalid(violation);
        return SubmitCoreAsync(JobTypes.Operations, Encoding.UTF8.GetBytes(CloudJson.Serialize(request)), idempotencyKey, principal, correlationId, ct);
    }

    public Task<SubmitNestJobResponse> SubmitPostAsync(
        SubmitPostJobRequest request,
        string idempotencyKey,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        if (CamRequestRules.Validate(request) is { } violation)
            Invalid(violation);
        return SubmitCoreAsync(JobTypes.Post, Encoding.UTF8.GetBytes(CloudJson.Serialize(request)), idempotencyKey, principal, correlationId, ct);
    }

    async Task<SubmitNestJobResponse> SubmitCoreAsync(
        string jobType,
        byte[] inputBytes,
        string idempotencyKey,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            Invalid("Exactly one nonempty Idempotency-Key of at most 200 characters is required.");
        var identity = RequestIdentity.From(principal);

        var inputSha256 = Sha256(inputBytes);
        var job = await jobs.CreateOrGetByIdempotencyKeyAsync(
            new NewComputeJob(
                identity.TenantId,
                identity.UserId,
                identity.DeviceId,
                jobType,
                idempotencyKey,
                correlationId,
                inputSha256),
            ct);
        if (!string.Equals(job.JobType, jobType, StringComparison.Ordinal))
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyConflict,
                "The Idempotency-Key was already used for a different job type.");
        }

        if (!string.Equals(job.InputSha256, inputSha256, StringComparison.Ordinal))
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.IdempotencyConflict,
                "The Idempotency-Key was already used with a different request.");
        }

        if (job.InputStoredAtUtc is null)
        {
            try
            {
                await objects.PutAsync(
                    job.InputObjectKey,
                    new MemoryStream(inputBytes, writable: false),
                    "application/json",
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                throw new ApiProblemException(
                    StatusCodes.Status500InternalServerError,
                    ApiErrorCodes.StorageFailed,
                    "The job input could not be stored.");
            }

            if (!await jobs.MarkInputStoredAsync(job.Id, ct))
            {
                throw new ApiProblemException(
                    StatusCodes.Status500InternalServerError,
                    ApiErrorCodes.InternalError,
                    "The job could not be queued.");
            }

            db.AuditEvents.Add(new AuditEventEntity
            {
                TenantId = identity.TenantId,
                UserId = identity.UserId,
                DeviceId = identity.DeviceId,
                JobId = job.Id,
                EventType = "job.submitted",
                CorrelationId = correlationId,
                CreatedAtUtc = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
        }

        return new SubmitNestJobResponse(job.Id, job.Status, correlationId);
    }

    public async Task<JobStatusResponse> GetStatusAsync(
        Guid jobId,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var identity = RequestIdentity.From(principal);
        var job = await jobs.GetAsync(jobId, identity.TenantId, ct) ?? NotFound();
        return new JobStatusResponse(
            job.Id,
            job.JobType,
            job.Status,
            job.AttemptCount,
            job.CreatedAtUtc,
            job.StartedAtUtc,
            job.CompletedAtUtc,
            job.ErrorCode,
            job.ErrorMessage,
            job.DurationMs,
            job.CorrelationId);
    }

    /// <summary>Exactly one member is set, matching the job type the client submitted.</summary>
    public sealed record ResultEnvelope(NestJobResult? V1, NestJobResultV2? V2, OperationsJobResult? Operations = null, PostJobResult? Post = null);

    public async Task<ResultEnvelope> GetResultAsync(
        Guid jobId,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var identity = RequestIdentity.From(principal);
        var job = await jobs.GetAsync(jobId, identity.TenantId, ct) ?? NotFound();
        if (job.Status != JobStatus.Succeeded)
        {
            throw new ApiProblemException(
                StatusCodes.Status409Conflict,
                ApiErrorCodes.JobNotReady,
                "The job result is not ready.");
        }
        if (job.ResultObjectKey is null
            || job.ResultSha256 is null
            || job.EngineVersion is null
            || job.DurationMs is null)
        {
            throw StorageFailed();
        }

        var bytes = await ReadVerifiedResultAsync(job, ct);
        try
        {
            var json = Encoding.UTF8.GetString(bytes);
            switch (job.JobType)
            {
                case JobTypes.NestV2:
                    return new ResultEnvelope(null, new NestJobResultV2(job.Id, CloudJson.Deserialize<NestJobResultPayloadV2>(json), job.EngineVersion, job.InputSha256, job.ResultSha256, job.DurationMs.Value));
                case JobTypes.Operations:
                    return new ResultEnvelope(null, null, new OperationsJobResult(job.Id, CloudJson.Deserialize<OperationsJobResultPayload>(json), job.EngineVersion, job.InputSha256, job.ResultSha256, job.DurationMs.Value));
                case JobTypes.Post:
                    return new ResultEnvelope(null, null, null, new PostJobResult(job.Id, CloudJson.Deserialize<PostJobResultPayload>(json), job.EngineVersion, job.InputSha256, job.ResultSha256, job.DurationMs.Value));
            }
            var payload = CloudJson.Deserialize<NestJobResultPayload>(json);
            return new ResultEnvelope(
                new NestJobResult(
                    job.Id, payload.Engine, job.EngineVersion, payload.Placements, payload.SheetCount, payload.Unplaced, payload.Warnings,
                    job.InputSha256, job.ResultSha256, job.DurationMs.Value),
                null);
        }
        catch (JsonException)
        {
            throw StorageFailed();
        }
    }

    async Task<byte[]> ReadVerifiedResultAsync(ComputeJobEntity job, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            await using var stream = await objects.OpenReadAsync(job.ResultObjectKey!, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw StorageFailed();
        }

        if (!string.Equals(Sha256(bytes), job.ResultSha256, StringComparison.Ordinal))
            throw StorageFailed();
        return bytes;
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    static ComputeJobEntity NotFound() =>
        throw new ApiProblemException(
            StatusCodes.Status404NotFound,
            ApiErrorCodes.JobNotFound,
            "The job was not found.");

    static ApiProblemException StorageFailed() =>
        new(
            StatusCodes.Status500InternalServerError,
            ApiErrorCodes.StorageFailed,
            "The stored job result is unavailable or invalid.");

    static void Invalid(string message) =>
        throw new ApiProblemException(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidRequest,
            message);

    sealed record RequestIdentity(Guid TenantId, Guid UserId, Guid DeviceId)
    {
        public static RequestIdentity From(ClaimsPrincipal principal)
        {
            if (!Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId)
                || !Guid.TryParse(principal.FindFirstValue("sub"), out var userId)
                || !Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId))
            {
                throw new ApiProblemException(
                    StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.Unauthorized,
                    "The access token identity is invalid.");
            }
            return new RequestIdentity(tenantId, userId, deviceId);
        }
    }
}
