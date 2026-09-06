namespace CabinetNC.Cloud.Contracts;

/// <summary>
/// Password login is the initial authentication only. <c>Tenant</c> is a human-entered server-side
/// slug/name used to locate the tenant; it is not a trusted TenantId (the JWT claim comes from DB).
/// <c>DeviceId</c> is the Desktop's persistent GUID (never derived from hardware);
/// <c>DeviceName</c> is a display label for the admin.
/// </summary>
public sealed record LoginRequest(
    string Tenant,
    string Email,
    string Password,
    string DeviceId,
    string? DeviceName);

/// <summary>
/// Token lifetimes are relative seconds rather than absolute timestamps so a Desktop with a skewed
/// clock still refreshes on time. The refresh token is opaque and shown to the client exactly once.
/// </summary>
public sealed record LoginResponse(
    string AccessToken,
    int AccessTokenExpiresInSeconds,
    string RefreshToken,
    int RefreshTokenExpiresInSeconds,
    string TenantId,
    string UserId,
    string DeviceId,
    string Role);

public sealed record RefreshRequest(
    string RefreshToken,
    string DeviceId);

/// <summary>Every successful refresh rotates: the old refresh token is revoked and a new one issued.</summary>
public sealed record RefreshResponse(
    string AccessToken,
    int AccessTokenExpiresInSeconds,
    string RefreshToken,
    int RefreshTokenExpiresInSeconds);

/// <summary>Revokes the given refresh token (and its device family). Sent with a bearer access token.</summary>
public sealed record LogoutRequest(
    string RefreshToken,
    string DeviceId);
