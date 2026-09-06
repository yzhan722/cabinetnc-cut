using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Infrastructure.Entities;

public sealed class TenantEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Unique per (TenantId, Email). Store emails lower-cased; the index is case-sensitive.</summary>
public sealed class UserEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>Unique per (TenantId, DeviceKey); DeviceKey is the Desktop's persistent GUID from <c>LoginRequest.DeviceId</c>.</summary>
public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string DeviceKey { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastSeenAtUtc { get; set; }
}

/// <summary>
/// Only the SHA-256 of the opaque token is stored. Every rotation issues a new row in the same
/// <see cref="FamilyId"/>; presenting a revoked member is reuse and revokes the whole family.
/// </summary>
public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}

/// <summary>
/// One compute job. Status is exactly the four spec states; a Queued row is only claimable once
/// <see cref="InputStoredAtUtc"/> is set, so a worker never sees a half-submitted job.
/// </summary>
public sealed class ComputeJobEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public required string JobType { get; set; }
    public JobStatus Status { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string CorrelationId { get; set; }
    public required string InputObjectKey { get; set; }
    public required string InputSha256 { get; set; }
    public DateTimeOffset? InputStoredAtUtc { get; set; }
    public string? ResultObjectKey { get; set; }
    public string? ResultSha256 { get; set; }
    public string? EngineVersion { get; set; }
    public int AttemptCount { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long? DurationMs { get; set; }
}

/// <summary>Append-only audit trail (auth.login.success, job.claimed, …). Never contains secrets.</summary>
public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? JobId { get; set; }
    public required string EventType { get; set; }
    public string? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
