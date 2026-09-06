using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Anonymous auth calls the session needs; implemented by <see cref="CloudApiClient"/>.</summary>
internal interface ICloudAuthTransport
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<RefreshResponse> RefreshAsync(RefreshRequest request, CancellationToken ct);
    Task LogoutAsync(LogoutRequest request, string accessToken, CancellationToken ct);
}

/// <summary>
/// Owns the access token (memory only) and the refresh token (memory + <see cref="ITokenStore"/>).
/// Refreshes proactively when the access token is about to expire and on demand after a 401, with all
/// concurrent callers sharing one refresh. A rejected refresh ends the session; a network failure does not.
/// </summary>
public sealed class AuthSession
{
    readonly ICloudAuthTransport _transport;
    readonly ITokenStore _tokens;
    readonly DeviceIdentityStore _devices;
    readonly CloudClientOptions _options;
    readonly TimeProvider _clock;
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    readonly object _stateGate = new();

    string? _accessToken;
    string? _refreshToken;
    DateTimeOffset _accessExpiresAt;

    internal AuthSession(ICloudAuthTransport transport, ITokenStore tokens, DeviceIdentityStore devices, CloudClientOptions options, TimeProvider clock)
    {
        _transport = transport;
        _tokens = tokens;
        _devices = devices;
        _options = options;
        _clock = clock;
    }

    public bool IsAuthenticated => _refreshToken is not null;
    public string? Email { get; private set; }
    public string? TenantId { get; private set; }
    public string? UserId { get; private set; }
    public string? Role { get; private set; }
    public DateTimeOffset? AccessTokenExpiresAtUtc => IsAuthenticated ? _accessExpiresAt : null;

    /// <summary>Raised after login, refresh, logout or a forced sign-out. May fire on any thread.</summary>
    public event Action? Changed;

    public string DeviceId => _devices.GetOrCreate().ToString("D");

    public async Task LoginAsync(string email, string password, CancellationToken ct)
    {
        var deviceId = DeviceId;
        var response = await _transport.LoginAsync(
            new LoginRequest(_options.Tenant, email, password, deviceId, Environment.MachineName), ct);

        var now = _clock.GetUtcNow();
        lock (_stateGate)
        {
            _accessToken = response.AccessToken;
            _accessExpiresAt = now.AddSeconds(response.AccessTokenExpiresInSeconds);
            _refreshToken = response.RefreshToken;
            Email = email;
            TenantId = response.TenantId;
            UserId = response.UserId;
            Role = response.Role;
        }
        _tokens.Save(new StoredRefreshToken(response.RefreshToken, deviceId, _options.Tenant, email, now.AddSeconds(response.RefreshTokenExpiresInSeconds)));
        Changed?.Invoke();
    }

    /// <summary>Resume from the stored refresh token. False (and a clean slate) when there is nothing usable.</summary>
    public async Task<bool> TryRestoreAsync(CancellationToken ct)
    {
        var stored = _tokens.Load();
        if (stored is null)
            return false;
        if (!string.Equals(stored.DeviceId, DeviceId, StringComparison.Ordinal)
            || !string.Equals(stored.Tenant, _options.Tenant, StringComparison.Ordinal)
            || stored.ExpiresAtUtc <= _clock.GetUtcNow())
        {
            _tokens.Clear();
            return false;
        }

        lock (_stateGate)
        {
            _refreshToken = stored.RefreshToken;
            _accessToken = null;
            _accessExpiresAt = DateTimeOffset.MinValue;
            Email = stored.Email;
        }

        try
        {
            await RefreshCoreAsync(ct);
            return true;
        }
        catch (CloudAuthenticationRequiredException)
        {
            return false;
        }
        catch (ComputeUnavailableException)
        {
            // Offline at startup: keep the restored refresh token; the next call will retry.
            return true;
        }
    }

    /// <summary>A valid access token, refreshing first when it is within the lead time of expiring.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_refreshToken is null)
                throw new CloudAuthenticationRequiredException("Not signed in to the intranet service.");
            if (_accessToken is not null && _clock.GetUtcNow() < _accessExpiresAt - _options.RefreshLeadTime)
                return _accessToken;
        }
        return await RefreshCoreAsync(ct);
    }

    /// <summary>Called after a 401: refresh unless somebody already replaced the rejected token.</summary>
    public async Task<string> ForceRefreshAsync(string? rejectedAccessToken, CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_refreshToken is null)
                throw new CloudAuthenticationRequiredException("Not signed in to the intranet service.");
            if (_accessToken is not null && !string.Equals(_accessToken, rejectedAccessToken, StringComparison.Ordinal))
                return _accessToken;
        }
        return await RefreshCoreAsync(ct, rejectedAccessToken);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        string? refresh, access;
        lock (_stateGate)
        {
            refresh = _refreshToken;
            access = _accessToken;
        }
        try
        {
            if (refresh is not null && access is not null)
                await _transport.LogoutAsync(new LogoutRequest(refresh, DeviceId), access, ct);
        }
        catch (Exception ex) when (ex is CloudApiException or ComputeUnavailableException)
        {
            // Local sign-out must always succeed; the refresh token dies at its own expiry.
        }
        finally
        {
            SignOut();
        }
    }

    async Task<string> RefreshCoreAsync(CancellationToken ct, string? rejectedAccessToken = null)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            string refreshToken;
            lock (_stateGate)
            {
                if (_refreshToken is null)
                    throw new CloudAuthenticationRequiredException("Not signed in to the intranet service.");
                // Another caller finished a refresh while we waited for the gate.
                if (_accessToken is not null
                    && !string.Equals(_accessToken, rejectedAccessToken, StringComparison.Ordinal)
                    && _clock.GetUtcNow() < _accessExpiresAt - _options.RefreshLeadTime)
                    return _accessToken;
                refreshToken = _refreshToken;
            }

            RefreshResponse response;
            try
            {
                response = await _transport.RefreshAsync(new RefreshRequest(refreshToken, DeviceId), ct);
            }
            catch (CloudApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                SignOut();
                throw new CloudAuthenticationRequiredException("The intranet session has ended; please sign in again.");
            }

            var now = _clock.GetUtcNow();
            string email, deviceId;
            lock (_stateGate)
            {
                _accessToken = response.AccessToken;
                _accessExpiresAt = now.AddSeconds(response.AccessTokenExpiresInSeconds);
                _refreshToken = response.RefreshToken;
                email = Email ?? "";
                deviceId = DeviceId;
            }
            _tokens.Save(new StoredRefreshToken(response.RefreshToken, deviceId, _options.Tenant, email, now.AddSeconds(response.RefreshTokenExpiresInSeconds)));
            Changed?.Invoke();
            return response.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    void SignOut()
    {
        lock (_stateGate)
        {
            _accessToken = null;
            _refreshToken = null;
            _accessExpiresAt = DateTimeOffset.MinValue;
            TenantId = null;
            UserId = null;
            Role = null;
        }
        _tokens.Clear();
        Changed?.Invoke();
    }
}
