using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Compute.Contracts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Progress the UI can show while a job is in flight.</summary>
public sealed record ComputeProgress(Guid JobId, JobStatus Status, int AttemptCount, TimeSpan Elapsed);

/// <summary>One nest computation, wherever it runs. Implementations never fall back to another mode.</summary>
public interface IComputeGateway
{
    ComputeMode Mode { get; }
    Task<NestJobResult> RunNestingAsync(SubmitNestJobRequest request, IProgress<ComputeProgress>? progress, CancellationToken ct);
}

/// <summary>
/// submit → JobId → poll (1 s, 2 s, … capped) → result. Transport failures are retried within
/// <see cref="CloudClientOptions.NetworkGrace"/> (the submit reuses its idempotency key, so a retry can
/// never create a second job); a Failed job or an exhausted timeout is reported, not hidden.
/// </summary>
public sealed class IntranetComputeGateway(
    CloudApiClient api,
    CloudClientOptions options,
    TimeProvider? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IComputeGateway
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public ComputeMode Mode => ComputeMode.Intranet;

    public async Task<NestJobResult> RunNestingAsync(SubmitNestJobRequest request, IProgress<ComputeProgress>? progress, CancellationToken ct)
    {
        var idempotencyKey = Guid.NewGuid().ToString("D");
        var started = _clock.GetUtcNow();

        var submitted = await WithNetworkGraceAsync(() => api.SubmitNestJobAsync(request, idempotencyKey, ct), ct);
        var jobId = submitted.JobId;
        progress?.Report(new ComputeProgress(jobId, submitted.Status, 0, TimeSpan.Zero));

        var interval = options.PollInitial;
        while (true)
        {
            var status = await WithNetworkGraceAsync(() => api.GetJobStatusAsync(jobId, ct), ct);
            var elapsed = _clock.GetUtcNow() - started;
            progress?.Report(new ComputeProgress(jobId, status.Status, status.AttemptCount, elapsed));

            switch (status.Status)
            {
                case JobStatus.Succeeded:
                    return await WithNetworkGraceAsync(() => api.GetJobResultAsync(jobId, ct), ct);
                case JobStatus.Failed:
                    throw new ComputeJobFailedException(jobId, status.ErrorCode,
                        $"{status.ErrorMessage ?? "The server could not compute this nest."} [{status.ErrorCode ?? "failed"}]");
            }

            if (elapsed >= options.JobTimeout)
                throw new ComputeJobTimeoutException(jobId, elapsed);

            await _delay(interval, ct);
            interval = interval + interval > options.PollMax ? options.PollMax : interval + interval;
        }
    }

    async Task<T> WithNetworkGraceAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        DateTimeOffset? firstFailure = null;
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (ComputeUnavailableException ex)
            {
                var now = _clock.GetUtcNow();
                firstFailure ??= now;
                if (now - firstFailure.Value >= options.NetworkGrace)
                    throw new ComputeUnavailableException(
                        $"The intranet service was unreachable for {options.NetworkGrace.TotalSeconds:0} s.", ex);
                await _delay(backoff, ct);
                backoff = backoff + backoff > options.PollMax ? options.PollMax : backoff + backoff;
            }
        }
    }
}

/// <summary>
/// Same contract, same runner, executed by the local gRPC worker. Exists for Local/Server A/B
/// comparison; hashes are computed exactly like the server does so results can be diffed by hash.
/// </summary>
public sealed class LocalComputeGateway(Func<Nesting.NestingClient> clientFactory, string workerVersion) : IComputeGateway
{
    public ComputeMode Mode => ComputeMode.Local;

    public async Task<NestJobResult> RunNestingAsync(SubmitNestJobRequest request, IProgress<ComputeProgress>? progress, CancellationToken ct)
    {
        var jobId = Guid.NewGuid();
        var inputBytes = Encoding.UTF8.GetBytes(CloudJson.Serialize(request));
        var grpcRequest = new StartNestingRequest
        {
            SheetWidthMm = request.SheetWidthMm,
            SheetLengthMm = request.SheetLengthMm,
            SpacingMm = request.SpacingMm,
            BorderMm = request.BorderMm,
            AllowRotation = request.AllowRotation,
        };
        foreach (var part in request.Parts)
        {
            grpcRequest.Parts.Add(new NestPartMsg
            {
                PanelId = part.PanelId,
                WidthMm = part.WidthMm,
                HeightMm = part.HeightMm,
                MayRotate = part.MayRotate,
                Material = part.Material ?? "",
                ThicknessMm = part.ThicknessMm,
            });
        }

        progress?.Report(new ComputeProgress(jobId, JobStatus.Running, 1, TimeSpan.Zero));
        var stopwatch = Stopwatch.StartNew();
        StartNestingReply reply;
        try
        {
            reply = await clientFactory().StartNestingAsync(grpcRequest, cancellationToken: ct);
        }
        catch (Grpc.Core.RpcException ex)
        {
            throw new ComputeUnavailableException($"The local worker did not answer ({ex.StatusCode}).", ex);
        }
        stopwatch.Stop();

        if (!reply.Ok)
            throw new ComputeJobFailedException(jobId, ApiErrorCodes.ComputeFailed, reply.Error);

        var payload = new NestJobResultPayload(
            reply.Engine,
            reply.Placements.Select(p => new NestPlacementDto(p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)).ToList(),
            reply.SheetCount,
            reply.Unplaced.ToList(),
            reply.Warnings.Select(w => new NestWarningDto(w.Code, w.Message, w.PanelIdA, w.PanelIdB, w.SheetIndex)).ToList());
        var resultBytes = Encoding.UTF8.GetBytes(CloudJson.Serialize(payload));

        progress?.Report(new ComputeProgress(jobId, JobStatus.Succeeded, 1, stopwatch.Elapsed));
        return new NestJobResult(
            jobId, payload.Engine, $"local-grpc/{workerVersion}", payload.Placements, payload.SheetCount, payload.Unplaced, payload.Warnings,
            Convert.ToHexStringLower(SHA256.HashData(inputBytes)), Convert.ToHexStringLower(SHA256.HashData(resultBytes)),
            stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>Resolves the gateway for a mode. Intranet requires a signed-in session; there is no silent fallback.</summary>
public sealed class ComputeGatewayFactory(Func<IComputeGateway> local, Func<CloudApiClient?> cloud)
{
    public IComputeGateway Create(ComputeMode mode)
    {
        switch (mode)
        {
            case ComputeMode.Local:
                return local();
            case ComputeMode.Intranet:
                var client = cloud() ?? throw new CloudAuthenticationRequiredException("Intranet mode is not configured; open 内网登录 first.");
                if (!client.Session.IsAuthenticated)
                    throw new CloudAuthenticationRequiredException("Not signed in to the intranet service.");
                return new IntranetComputeGateway(client, client.Options);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }
}
