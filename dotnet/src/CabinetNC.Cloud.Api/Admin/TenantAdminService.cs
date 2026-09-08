using System.Security.Claims;
using System.Text.Json;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CabinetNC.Cloud.Api.Admin;

/// <summary>
/// Tenant-scoped administration of users, devices and sessions. Every mutation is audited with the
/// acting user; passwords are only ever hashed; revoking means the refresh tokens die now and the
/// access token dies at its 15-minute expiry.
/// </summary>
public sealed class TenantAdminService(CloudDbContext db, PasswordVerifier passwords, TimeProvider clock)
{
    public sealed record Actor(Guid TenantId, Guid UserId, Guid DeviceId, string Role)
    {
        public static Actor From(ClaimsPrincipal user) => new(
            Guid.Parse(user.FindFirstValue("tenant_id")!),
            Guid.Parse(user.FindFirstValue("sub")!),
            Guid.Parse(user.FindFirstValue("device_id")!),
            user.FindFirstValue("role") ?? "");
    }

    public async Task<UserSummary> CreateUserAsync(CreateUserRequest request, Actor actor, string correlationId, CancellationToken ct)
    {
        var email = BootstrapAdmin.NormalizeEmail(request.Email ?? "");
        if (email.Length is 0 or > 320 || !email.Contains('@', StringComparison.Ordinal) || email.Contains(' ', StringComparison.Ordinal))
            throw Invalid("A valid email address is required.");
        var role = NormalizeRole(request.Role);
        if (PasswordPolicy.Check(request.Password, email) is { } reason)
            throw Invalid(reason);
        if (await db.Users.AnyAsync(u => u.TenantId == actor.TenantId && u.Email == email, ct))
            throw Conflict("A user with this email already exists.");

        var user = new UserEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            Email = email,
            PasswordHash = "",
            Role = role,
            IsActive = true,
            CreatedAtUtc = clock.GetUtcNow(),
        };
        user.PasswordHash = passwords.Hash(user, request.Password!);
        db.Users.Add(user);
        Audit("user.created", actor, user.Id, null, correlationId, new { role });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Conflict("A user with this email already exists.");
        }
        return await SummaryAsync(user.Id, actor.TenantId, ct) ?? throw new InvalidOperationException("created user vanished");
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(Actor actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Users.AsNoTracking()
            .Where(u => u.TenantId == actor.TenantId)
            .OrderBy(u => u.Email)
            .Select(u => new UserSummary(
                u.Id, u.Email, u.Role, u.IsActive, u.CreatedAtUtc,
                db.Devices.Count(d => d.UserId == u.Id),
                db.RefreshTokens.Count(t => t.UserId == u.Id && t.RevokedAtUtc == null && t.ExpiresAtUtc > now),
                db.Devices.Where(d => d.UserId == u.Id).Max(d => d.LastSeenAtUtc)))
            .ToListAsync(ct);
    }

    public async Task<UserSummary> UpdateUserAsync(Guid userId, UpdateUserRequest request, Actor actor, string correlationId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.TenantId == actor.TenantId, ct)
                   ?? throw NotFound();
        var newRole = request.Role is null ? user.Role : NormalizeRole(request.Role);
        var newActive = request.IsActive ?? user.IsActive;

        var losesAdmin = user.IsActive && user.Role == UserRoles.Admin && (newRole != UserRoles.Admin || !newActive);
        if (losesAdmin)
        {
            var otherActiveAdmins = await db.Users.CountAsync(
                u => u.TenantId == actor.TenantId && u.Id != user.Id && u.IsActive && u.Role == UserRoles.Admin, ct);
            if (otherActiveAdmins == 0)
                throw Conflict("At least one active administrator must remain.");
        }

        user.Role = newRole;
        user.IsActive = newActive;
        var revoked = 0;
        if (!newActive)
            revoked = await RevokeAsync(t => t.UserId == user.Id, ct);
        Audit("user.updated", actor, user.Id, null, correlationId, new { role = newRole, isActive = newActive, revokedSessions = revoked });
        await db.SaveChangesAsync(ct);
        return await SummaryAsync(user.Id, actor.TenantId, ct) ?? throw NotFound();
    }

    public async Task ResetPasswordAsync(Guid userId, ResetPasswordRequest request, Actor actor, string correlationId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.TenantId == actor.TenantId, ct)
                   ?? throw NotFound();
        if (PasswordPolicy.Check(request.NewPassword, user.Email) is { } reason)
            throw Invalid(reason);
        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        var revoked = await RevokeAsync(t => t.UserId == user.Id, ct);
        Audit("user.password_reset", actor, user.Id, null, correlationId, new { revokedSessions = revoked });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RevokeResponse> RevokeUserSessionsAsync(Guid userId, Actor actor, string correlationId, CancellationToken ct)
    {
        var exists = await db.Users.AnyAsync(u => u.Id == userId && u.TenantId == actor.TenantId, ct);
        if (!exists)
            throw NotFound();
        var revoked = await RevokeAsync(t => t.UserId == userId, ct);
        Audit("user.sessions_revoked", actor, userId, null, correlationId, new { revokedSessions = revoked });
        await db.SaveChangesAsync(ct);
        return new RevokeResponse(revoked);
    }

    public async Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync(Guid? userId, Actor actor, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var query = db.Devices.AsNoTracking().Where(d => d.TenantId == actor.TenantId);
        if (userId is not null)
            query = query.Where(d => d.UserId == userId);
        return await query
            .OrderBy(d => d.CreatedAtUtc)
            .Select(d => new DeviceSummary(
                d.Id, d.UserId,
                db.Users.Where(u => u.Id == d.UserId).Select(u => u.Email).First(),
                d.DeviceKey, d.DeviceName, d.CreatedAtUtc, d.LastSeenAtUtc,
                db.RefreshTokens.Count(t => t.DeviceId == d.Id && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)))
            .ToListAsync(ct);
    }

    public async Task<RevokeResponse> RevokeDeviceAsync(Guid deviceId, Actor actor, string correlationId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId && d.TenantId == actor.TenantId, ct)
                     ?? throw new ApiProblemException(StatusCodes.Status404NotFound, ApiErrorCodes.InvalidDevice, "The device does not exist.");
        var revoked = await RevokeAsync(t => t.DeviceId == deviceId, ct);
        Audit("device.revoked", actor, device.UserId, device.Id, correlationId, new { revokedSessions = revoked });
        await db.SaveChangesAsync(ct);
        return new RevokeResponse(revoked);
    }

    /// <summary>Self-service: verify the current password, set the new one, sign out every other device.</summary>
    public async Task ChangeOwnPasswordAsync(ChangePasswordRequest request, Actor actor, string correlationId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == actor.UserId && u.TenantId == actor.TenantId && u.IsActive, ct);
        if (passwords.Verify(user, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed || user is null)
        {
            Audit("user.password_change_failed", actor, actor.UserId, actor.DeviceId, correlationId, null);
            await db.SaveChangesAsync(ct);
            throw new ApiProblemException(StatusCodes.Status401Unauthorized, ApiErrorCodes.InvalidCredentials, "The current password is incorrect.");
        }
        if (PasswordPolicy.Check(request.NewPassword, user.Email) is { } reason)
            throw Invalid(reason);
        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        var revoked = await RevokeAsync(t => t.UserId == user.Id && t.DeviceId != actor.DeviceId, ct);
        Audit("user.password_changed", actor, user.Id, actor.DeviceId, correlationId, new { revokedOtherSessions = revoked });
        await db.SaveChangesAsync(ct);
    }

    async Task<UserSummary?> SummaryAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .Select(u => new UserSummary(
                u.Id, u.Email, u.Role, u.IsActive, u.CreatedAtUtc,
                db.Devices.Count(d => d.UserId == u.Id),
                db.RefreshTokens.Count(t => t.UserId == u.Id && t.RevokedAtUtc == null && t.ExpiresAtUtc > now),
                db.Devices.Where(d => d.UserId == u.Id).Max(d => d.LastSeenAtUtc)))
            .SingleOrDefaultAsync(ct);
    }

    Task<int> RevokeAsync(System.Linq.Expressions.Expression<Func<RefreshTokenEntity, bool>> scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(scope)
            .Where(t => t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, now), ct);
    }

    void Audit(string eventType, Actor actor, Guid? targetUserId, Guid? deviceId, string correlationId, object? details)
    {
        var payload = new Dictionary<string, object?> { ["actorUserId"] = actor.UserId, ["actorDeviceId"] = actor.DeviceId };
        if (details is not null)
        {
            foreach (var property in details.GetType().GetProperties())
                payload[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(details);
        }
        db.AuditEvents.Add(new AuditEventEntity
        {
            TenantId = actor.TenantId,
            UserId = targetUserId,
            DeviceId = deviceId,
            EventType = eventType,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(payload, CloudJson.Options),
            CreatedAtUtc = clock.GetUtcNow(),
        });
    }

    static string NormalizeRole(string? role)
    {
        var normalized = (role ?? "").Trim().ToLowerInvariant();
        if (!UserRoles.All.Contains(normalized, StringComparer.Ordinal))
            throw Invalid($"Role must be one of: {string.Join(", ", UserRoles.All)}.");
        return normalized;
    }

    static ApiProblemException Invalid(string message) => new(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, message);
    static ApiProblemException Conflict(string message) => new(StatusCodes.Status409Conflict, ApiErrorCodes.Conflict, message);
    static ApiProblemException NotFound() => new(StatusCodes.Status404NotFound, ApiErrorCodes.InvalidRequest, "The user does not exist.");
}
