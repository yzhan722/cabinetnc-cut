namespace CabinetNC.Cloud.Contracts;

/// <summary>Body of <c>GET /api/v1/health</c>. Liveness only; it does not fail when the database is down.</summary>
public sealed record HealthResponse(
    string Status,
    string Service,
    string Version,
    DateTimeOffset TimestampUtc);

/// <summary>One dependency probe: "ok" or "failed" plus a non-sensitive reason and the probe latency.</summary>
public sealed record DependencyHealth(string Status, long LatencyMs, string? Reason);

/// <summary>Readiness: "ready" when every dependency is ok; served with HTTP 503 otherwise.</summary>
public sealed record ReadinessResponse(
    string Status,
    string Version,
    DateTimeOffset TimestampUtc,
    DependencyHealth Database,
    DependencyHealth ObjectStore,
    int QueuedJobs,
    int RunningJobs);

/// <summary>The only job states the spec allows. Serialized as these exact strings.</summary>
public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

/// <summary>Rectangular (AABB) part; mirrors the local worker's nest contract field for field.</summary>
public sealed record NestPartDto(
    string PanelId,
    double WidthMm,
    double HeightMm,
    bool MayRotate,
    string? Material,
    double ThicknessMm);

/// <summary>
/// Body of <c>POST /api/v1/jobs/nest</c>. The API stores the canonical JSON of this object and
/// hashes it into <see cref="NestJobResult.InputSha256"/>. Tenant/user/device come from the token, never from here.
/// </summary>
public sealed record SubmitNestJobRequest(
    IReadOnlyList<NestPartDto> Parts,
    double SheetWidthMm,
    double SheetLengthMm,
    double SpacingMm,
    double BorderMm,
    bool AllowRotation);

/// <summary>202 Accepted body. Nest runs on the worker, never inside the request thread.</summary>
public sealed record SubmitNestJobResponse(
    Guid JobId,
    JobStatus Status,
    string CorrelationId);

/// <summary>Body of <c>GET /api/v1/jobs/{jobId}</c>. <c>ErrorCode</c> is one of <see cref="ApiErrorCodes"/> when Failed.</summary>
public sealed record JobStatusResponse(
    Guid JobId,
    string JobType,
    JobStatus Status,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    long? DurationMs,
    string CorrelationId);

public sealed record NestPlacementDto(
    string PanelId,
    int SheetIndex,
    double OffsetX,
    double OffsetY,
    double RotationDeg);

/// <summary>
/// <c>Code</c> is <c>engine_fallback</c> (panel/sheet fields null) or <c>aabb_gap</c>, exactly as the
/// local worker reports them, so Local/Server parity can compare warnings too.
/// </summary>
public sealed record NestWarningDto(
    string Code,
    string Message,
    string? PanelIdA,
    string? PanelIdB,
    int? SheetIndex);

/// <summary>
/// Canonical object stored in MinIO. JobId/hashes/version/duration stay in PostgreSQL and are added
/// by the result endpoint, avoiding a circular ResultSha256 field inside the bytes being hashed.
/// </summary>
/// <summary>One audit row as exposed to administrators. Details are the JSON the server recorded.</summary>
public sealed record AuditEventDto(
    long Id,
    string EventType,
    DateTimeOffset CreatedAtUtc,
    string? CorrelationId,
    string? DetailsJson);

/// <summary>
/// Admin diagnostics for a job: identity, state, hashes, engine, timings, error, audit trail.
/// Deliberately contains no credential material of any kind.
/// </summary>
public sealed record JobDiagnosticsResponse(
    Guid JobId,
    Guid TenantId,
    string TenantName,
    Guid UserId,
    string UserEmail,
    Guid DeviceId,
    string DeviceKey,
    string? DeviceName,
    string JobType,
    JobStatus Status,
    string CorrelationId,
    string IdempotencyKey,
    string InputObjectKey,
    string InputSha256,
    string? ResultObjectKey,
    string? ResultSha256,
    string? EngineVersion,
    int AttemptCount,
    string? LockedBy,
    DateTimeOffset? LockedUntilUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? InputStoredAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<AuditEventDto> AuditEvents);

public sealed record NestJobResultPayload(
    string Engine,
    IReadOnlyList<NestPlacementDto> Placements,
    int SheetCount,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<NestWarningDto> Warnings);

/// <summary>
/// Body of <c>GET /api/v1/jobs/{jobId}/result</c>, available only once the job Succeeded
/// (<c>job_not_ready</c> before that). Hashes are hex SHA-256 of the stored input/result objects.
/// </summary>
public sealed record NestJobResult(
    Guid JobId,
    string Engine,
    string EngineVersion,
    IReadOnlyList<NestPlacementDto> Placements,
    int SheetCount,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<NestWarningDto> Warnings,
    string InputSha256,
    string ResultSha256,
    long DurationMs);
