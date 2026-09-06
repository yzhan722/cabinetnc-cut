using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;

namespace CabinetNC.Cloud.Infrastructure.Jobs;

/// <summary>Identity and payload hash of a job the API wants to enqueue; the repository assigns Id and object key.</summary>
public sealed record NewComputeJob(
    Guid TenantId,
    Guid UserId,
    Guid DeviceId,
    string JobType,
    string IdempotencyKey,
    string CorrelationId,
    string InputSha256);

/// <summary>A claimed job. <c>WorkerId</c> is the fencing token for <c>MarkSucceeded</c>/<c>MarkFailed</c>.</summary>
public sealed record JobLease(ComputeJobEntity Job, string WorkerId, DateTimeOffset LockedUntilUtc);

public sealed record JobCompletion(string ResultObjectKey, string ResultSha256, string EngineVersion, long DurationMs);

public interface IJobRepository
{
    /// <summary>
    /// Inserts a Queued job or returns the existing one for the same (tenant, user, idempotency key).
    /// Callers compare <c>InputSha256</c> on the returned row to detect <c>idempotency_conflict</c>, and
    /// upload + <see cref="MarkInputStoredAsync"/> when <c>InputStoredAtUtc</c> is still null.
    /// </summary>
    Task<ComputeJobEntity> CreateOrGetByIdempotencyKeyAsync(NewComputeJob job, CancellationToken ct);

    /// <summary>Makes the job claimable once its input object is durably stored. Idempotent; false if the job does not exist.</summary>
    Task<bool> MarkInputStoredAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// Atomically claims the oldest claimable job (Queued with stored input, or Running with an expired lease)
    /// using <c>FOR UPDATE SKIP LOCKED</c>: Status→Running, AttemptCount+1, LockedBy/LockedUntilUtc set.
    /// Jobs whose lease expired after the last allowed attempt are marked Failed instead. Null when nothing is claimable.
    /// </summary>
    Task<JobLease?> TryClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct);

    /// <summary>Records the result only if <paramref name="workerId"/> still holds the lease; false means the lease was lost.</summary>
    Task<bool> MarkSucceededAsync(Guid jobId, string workerId, JobCompletion completion, CancellationToken ct);

    /// <summary>
    /// Records a failure if <paramref name="workerId"/> still holds the lease. Retryable failures under the
    /// attempt budget requeue the job (returns Queued); otherwise it is final (returns Failed). Null means the lease was lost.
    /// </summary>
    Task<JobStatus?> MarkFailedAsync(Guid jobId, string workerId, string errorCode, string errorMessage, bool retryable, CancellationToken ct);

    /// <summary>Tenant-scoped read; a job from another tenant is indistinguishable from a missing one.</summary>
    Task<ComputeJobEntity?> GetAsync(Guid jobId, Guid tenantId, CancellationToken ct);
}
