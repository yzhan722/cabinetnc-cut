using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CabinetNC.Cloud.Api.Auth;

public sealed class AuthService(
    CloudDbContext db,
    CloudApiOptions options,
    TimeProvider clock,
    PasswordVerifier passwords,
    AccessTokenIssuer accessTokens)
{
    const int MaxPasswordChars = 1024;
    const int MaxRefreshTokenChars = 512;

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Tenant)
            || request.Tenant.Length > 200
            || string.IsNullOrWhiteSpace(request.Email)
            || request.Email.Length > 320
            || string.IsNullOrWhiteSpace(request.Password)
            || request.Password.Length > MaxPasswordChars
            || request.DeviceName?.Length > 200)
        {
            throw InvalidRequest("Tenant, email, password, and a GUID deviceId are required.");
        }
        var tenantName = BootstrapAdmin.NormalizeTenant(request.Tenant);
        var email = BootstrapAdmin.NormalizeEmail(request.Email ?? "");
        var deviceKey = NormalizeDeviceKey(request.DeviceId);
        if (tenantName.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
            throw InvalidRequest("Tenant or email is invalid.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // The request carries a tenant slug, never a TenantId. Resolve the authoritative id from DB,
        // then every token/device row uses that id.
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Name == tenantName, ct);
        var user = tenant is null
            ? null
            : await db.Users.SingleOrDefaultAsync(
                u => u.TenantId == tenant.Id && u.Email == email && u.IsActive,
                ct);
        var verified = passwords.Verify(user, request.Password);
        if (user is null || verified == PasswordVerificationResult.Failed)
        {
            AddAudit(
                "auth.login.failed",
                tenant?.Id ?? Guid.Empty,
                user?.Id,
                null,
                correlationId,
                ApiErrorCodes.InvalidCredentials);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiProblemException(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidCredentials,
                "The email or password is invalid.");
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = passwords.Hash(user, request.Password);

        var now = clock.GetUtcNow();
        var device = await UpsertDeviceAsync(
            user.TenantId,
            user.Id,
            deviceKey,
            CleanDeviceName(request.DeviceName),
            now,
            ct);
        if (device.UserId != user.Id)
        {
            AddAudit(
                "auth.login.failed",
                user.TenantId,
                user.Id,
                device.Id,
                correlationId,
                ApiErrorCodes.InvalidDevice);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiProblemException(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidDevice,
                "The device belongs to another user.");
        }

        var (rawRefresh, refresh) = NewRefreshToken(user, device, Guid.CreateVersion7(), now);
        db.RefreshTokens.Add(refresh);
        AddAudit("auth.login.success", user.TenantId, user.Id, device.Id, correlationId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new LoginResponse(
            AccessToken: accessTokens.Issue(new(user.Id, user.TenantId, device.Id, user.Role)),
            AccessTokenExpiresInSeconds: checked((int)options.AccessTokenLifetime.TotalSeconds),
            RefreshToken: rawRefresh,
            RefreshTokenExpiresInSeconds: checked((int)options.RefreshTokenLifetime.TotalSeconds),
            TenantId: user.TenantId.ToString("D"),
            UserId: user.Id.ToString("D"),
            DeviceId: device.DeviceKey,
            Role: user.Role);
    }

    public async Task<RefreshResponse> RefreshAsync(
        RefreshRequest request,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)
            || request.RefreshToken.Length > MaxRefreshTokenChars)
            throw InvalidRequest("RefreshToken and a GUID deviceId are required.");
        var deviceKey = NormalizeDeviceKey(request.DeviceId);
        var tokenHash = HashToken(request.RefreshToken);
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var token = await FindAndLockTokenFamilyAsync(tokenHash, ct);
        if (token is null)
        {
            AddAudit(
                "auth.refresh.failed",
                Guid.Empty,
                null,
                null,
                correlationId,
                ApiErrorCodes.RefreshInvalid);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw RefreshInvalid();
        }

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == token.DeviceId, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == token.UserId && u.IsActive, ct);
        if (device is null
            || user is null
            || device.TenantId != token.TenantId
            || device.UserId != token.UserId
            || user.TenantId != token.TenantId)
        {
            AddAudit(
                "auth.refresh.failed",
                token.TenantId,
                token.UserId,
                token.DeviceId,
                correlationId,
                ApiErrorCodes.RefreshInvalid);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw RefreshInvalid();
        }
        if (!string.Equals(device.DeviceKey, deviceKey, StringComparison.Ordinal))
        {
            AddAudit(
                "auth.refresh.failed",
                token.TenantId,
                token.UserId,
                token.DeviceId,
                correlationId,
                ApiErrorCodes.InvalidDevice);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiProblemException(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.InvalidDevice,
                "The refresh token does not belong to this device.");
        }

        if (token.RevokedAtUtc is not null)
        {
            if (token.ReplacedByTokenId is not null)
            {
                await RevokeFamilyAsync(token.FamilyId, now, ct);
                AddAudit(
                    "auth.refresh.failed",
                    token.TenantId,
                    token.UserId,
                    token.DeviceId,
                    correlationId,
                    "reuse");
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                throw new ApiProblemException(
                    StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.RefreshReuseDetected,
                    "Refresh token reuse was detected; sign in again.");
            }

            AddAudit(
                "auth.refresh.failed",
                token.TenantId,
                token.UserId,
                token.DeviceId,
                correlationId,
                ApiErrorCodes.RefreshInvalid);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw RefreshInvalid();
        }

        if (token.ExpiresAtUtc <= now)
        {
            token.RevokedAtUtc = now;
            AddAudit(
                "auth.refresh.failed",
                token.TenantId,
                token.UserId,
                token.DeviceId,
                correlationId,
                "expired");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw RefreshInvalid();
        }

        var (rawRefresh, successor) = NewRefreshToken(user, device, token.FamilyId, now);
        token.RevokedAtUtc = now;
        token.ReplacedByTokenId = successor.Id;
        device.LastSeenAtUtc = now;
        db.RefreshTokens.Add(successor);
        AddAudit("auth.refresh.success", token.TenantId, token.UserId, token.DeviceId, correlationId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new RefreshResponse(
            AccessToken: accessTokens.Issue(new(user.Id, user.TenantId, device.Id, user.Role)),
            AccessTokenExpiresInSeconds: checked((int)options.AccessTokenLifetime.TotalSeconds),
            RefreshToken: rawRefresh,
            RefreshTokenExpiresInSeconds: checked((int)options.RefreshTokenLifetime.TotalSeconds));
    }

    public async Task LogoutAsync(
        LogoutRequest request,
        ClaimsPrincipal principal,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)
            || request.RefreshToken.Length > MaxRefreshTokenChars)
            throw InvalidRequest("RefreshToken and a GUID deviceId are required.");
        var deviceKey = NormalizeDeviceKey(request.DeviceId);
        if (!TryReadSubject(principal, out var tenantId, out var userId, out var deviceId))
            throw new ApiProblemException(
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthorized,
                "The access token is invalid.");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var token = await FindAndLockTokenFamilyAsync(HashToken(request.RefreshToken), ct);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, ct);
        if (token is not null
            && device is not null
            && token.TenantId == tenantId
            && token.UserId == userId
            && token.DeviceId == deviceId
            && device.TenantId == tenantId
            && device.UserId == userId
            && string.Equals(device.DeviceKey, deviceKey, StringComparison.Ordinal))
        {
            await RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow(), ct);
        }

        AddAudit("auth.logout", tenantId, userId, deviceId, correlationId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    async Task<DeviceEntity> UpsertDeviceAsync(
        Guid tenantId,
        Guid userId,
        string deviceKey,
        string? deviceName,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var proposedId = Guid.CreateVersion7();
        var name = deviceName ?? "";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Devices"
                ("Id", "TenantId", "UserId", "DeviceKey", "DeviceName", "CreatedAtUtc", "LastSeenAtUtc")
            VALUES
                ({proposedId}, {tenantId}, {userId}, {deviceKey}, NULLIF({name}, ''), {now}, {now})
            ON CONFLICT ("TenantId", "DeviceKey") DO UPDATE
            SET "DeviceName" = COALESCE(EXCLUDED."DeviceName", "Devices"."DeviceName"),
                "LastSeenAtUtc" = EXCLUDED."LastSeenAtUtc"
            WHERE "Devices"."UserId" = EXCLUDED."UserId"
            """, ct);

        // If another user already owns this tenant/device key the conflict UPDATE affects zero rows;
        // return that row so the caller can audit and reject it without exposing the owner.
        return await db.Devices.SingleAsync(
            d => d.TenantId == tenantId && d.DeviceKey == deviceKey,
            ct);
    }

    async Task<RefreshTokenEntity?> FindAndLockTokenFamilyAsync(string tokenHash, CancellationToken ct)
    {
        var familyId = await db.RefreshTokens
            .AsNoTracking()
            .Where(token => token.TokenHash == tokenHash)
            .Select(token => (Guid?)token.FamilyId)
            .SingleOrDefaultAsync(ct);
        if (familyId is null)
            return null;

        // Serialize *all* operations in one family, not only operations on one token row. Without
        // this, old-token reuse could race a successor refresh and miss the newly inserted successor
        // due to PostgreSQL Read Committed statement snapshots.
        var lockKey = FamilyLockKey(familyId.Value);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            ct);
        return await FindTokenForUpdateAsync(tokenHash, ct);
    }

    async Task<RefreshTokenEntity?> FindTokenForUpdateAsync(string tokenHash, CancellationToken ct) =>
        await db.RefreshTokens
            .FromSqlInterpolated($"""
                SELECT * FROM "RefreshTokens"
                WHERE "TokenHash" = {tokenHash}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);

    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAtUtc, now), ct);

    (string Raw, RefreshTokenEntity Entity) NewRefreshToken(
        UserEntity user,
        DeviceEntity device,
        Guid familyId,
        DateTimeOffset now)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        return (raw, new RefreshTokenEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            UserId = user.Id,
            DeviceId = device.Id,
            FamilyId = familyId,
            TokenHash = HashToken(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(options.RefreshTokenLifetime),
        });
    }

    void AddAudit(
        string eventType,
        Guid tenantId,
        Guid? userId,
        Guid? deviceId,
        string correlationId,
        string? reason = null)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            TenantId = tenantId,
            UserId = userId,
            DeviceId = deviceId,
            EventType = eventType,
            CorrelationId = correlationId,
            DetailsJson = reason is null
                ? null
                : JsonSerializer.Serialize(new Dictionary<string, string> { ["reason"] = reason }, CloudJson.Options),
            CreatedAtUtc = clock.GetUtcNow(),
        });
    }

    static string NormalizeDeviceKey(string? value)
    {
        if (!Guid.TryParse(value, out var id))
            throw InvalidRequest("A valid GUID deviceId is required.");
        return id.ToString("D");
    }

    static string? CleanDeviceName(string? value)
    {
        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean))
            return null;
        return clean.Length <= 200 ? clean : clean[..200];
    }

    public static string HashToken(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    internal static long FamilyLockKey(Guid familyId) =>
        BitConverter.ToInt64(familyId.ToByteArray(), 0);

    static bool TryReadSubject(
        ClaimsPrincipal principal,
        out Guid tenantId,
        out Guid userId,
        out Guid deviceId)
    {
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        deviceId = Guid.Empty;
        return Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId)
               && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
                                ?? principal.FindFirstValue("sub"), out userId)
               && Guid.TryParse(principal.FindFirstValue("device_id"), out deviceId);
    }

    static ApiProblemException InvalidRequest(string message) =>
        new(StatusCodes.Status400BadRequest, ApiErrorCodes.InvalidRequest, message);

    static ApiProblemException RefreshInvalid() =>
        new(
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.RefreshInvalid,
            "The refresh token is invalid or expired.");
}
