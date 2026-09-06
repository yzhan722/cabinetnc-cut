using System.Net;
using System.Security.Cryptography;
using System.Text;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Tests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>
/// End-to-end over HTTP against the real API and a real PostgreSQL: bootstrap admin, password login,
/// 15-minute access tokens with the required claims, rotating refresh tokens with reuse detection,
/// logout, correlation ids, rate limits. Every test boots a fresh app on truncated tables.
/// </summary>
public class AuthEndpointTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    static readonly string DeviceA = "3b6d8d38-1d1a-4a8e-9c1f-0f8a2c0f1e11";
    static readonly string DeviceB = "9c0e2f4a-6b7d-4e1f-8a2b-3c4d5e6f7a8b";

    readonly ManualClock _clock = new() { Now = TruncateToSeconds(DateTimeOffset.UtcNow) };
    CloudApiFactory _factory = null!;
    HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;
        await pg.ResetAsync();
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, TimeSpan.Zero);

    static LoginRequest Login(
        string device = "",
        string? password = null,
        string? email = null,
        string? tenant = null) =>
        new(
            tenant ?? CloudApiFactory.Tenant,
            email ?? CloudApiFactory.AdminEmail,
            password ?? CloudApiFactory.AdminPassword,
            device == "" ? DeviceA : device,
            "SHOP-PC-01");

    async Task<LoginResponse> LoginOkAsync(string device = "")
    {
        var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin, Login(device));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTokenResponseIsNotCacheable(response);
        return await response.ReadAsync<LoginResponse>();
    }

    static void AssertTokenResponseIsNotCacheable(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(response.Headers.Pragma, value =>
            string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));
    }

    static string Sha256Hex(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    [PostgresFact]
    public async Task Health_returns_ok_and_every_response_carries_a_correlation_id()
    {
        var response = await _client.GetAsync(ApiRoutes.Health);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.ReadAsync<HealthResponse>();
        Assert.Equal("ok", health.Status);
        Assert.Equal("cabinetnc-cloud-api", health.Service);
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
        var generated = Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId));
        Assert.False(string.IsNullOrWhiteSpace(generated));

        // A caller-supplied id is echoed back, including on error responses.
        const string callerCorrelationId = "0198f4c0-4c1b-7a12-8c4d-123456789abc";
        var echoed = await _client.PostJsonAsync(
            ApiRoutes.AuthLogin,
            Login(password: "wrong"),
            correlationId: callerCorrelationId);
        Assert.Equal(callerCorrelationId, Assert.Single(echoed.Headers.GetValues(ApiHeaders.CorrelationId)));
        Assert.Equal(callerCorrelationId, (await echoed.ReadErrorAsync()).CorrelationId);

        var missing = await _client.GetAsync("/api/v1/not-a-real-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingError = await missing.ReadErrorAsync();
        Assert.Equal(ApiErrorCodes.InvalidRequest, missingError.Code);
        Assert.Equal(
            Assert.Single(missing.Headers.GetValues(ApiHeaders.CorrelationId)),
            missingError.CorrelationId);
    }

    [PostgresFact]
    public async Task Bootstrap_creates_tenant_and_admin_from_environment_without_storing_the_password()
    {
        await using var db = pg.CreateContext();

        var tenant = Assert.Single(await db.Tenants.ToListAsync());
        Assert.Equal(CloudApiFactory.Tenant, tenant.Name);
        var admin = Assert.Single(await db.Users.ToListAsync());
        Assert.Equal(tenant.Id, admin.TenantId);
        Assert.Equal(CloudApiFactory.AdminEmail, admin.Email);
        Assert.Equal("admin", admin.Role);
        Assert.True(admin.IsActive);
        Assert.NotEqual(CloudApiFactory.AdminPassword, admin.PasswordHash);
        Assert.DoesNotContain(CloudApiFactory.AdminPassword, admin.PasswordHash);

        // A second boot on a populated database must not recreate or overwrite anything.
        await using var second = new CloudApiFactory(pg.ConnectionString, _clock);
        using var client = second.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ApiRoutes.Health)).StatusCode);
        Assert.Equal(1, await db.Tenants.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [PostgresFact]
    public async Task Omitting_all_bootstrap_variables_creates_no_default_credentials()
    {
        await pg.ResetAsync();
        await using var noBootstrap = new CloudApiFactory(pg.ConnectionString, _clock, includeBootstrap: false);
        using var client = noBootstrap.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ApiRoutes.Health)).StatusCode);
        await using var db = pg.CreateContext();
        Assert.Empty(await db.Tenants.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
    }

    [PostgresFact]
    public async Task Two_api_replicas_bootstrapping_concurrently_create_one_admin()
    {
        await pg.ResetAsync();
        await using var first = new CloudApiFactory(pg.ConnectionString, _clock);
        await using var second = new CloudApiFactory(pg.ConnectionString, _clock);

        var statuses = await Task.WhenAll(
            Task.Run(async () =>
            {
                using var client = first.CreateClient();
                return (await client.GetAsync(ApiRoutes.Health)).StatusCode;
            }),
            Task.Run(async () =>
            {
                using var client = second.CreateClient();
                return (await client.GetAsync(ApiRoutes.Health)).StatusCode;
            }));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        await using var db = pg.CreateContext();
        Assert.Single(await db.Tenants.ToListAsync());
        Assert.Single(await db.Users.ToListAsync());
    }

    [PostgresFact]
    public async Task Login_success_returns_tokens_identity_and_lifetimes()
    {
        var login = await LoginOkAsync();

        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.Equal(15 * 60, login.AccessTokenExpiresInSeconds);
        Assert.Equal(30 * 24 * 3600, login.RefreshTokenExpiresInSeconds);
        Assert.Equal(DeviceA, login.DeviceId);
        Assert.Equal("admin", login.Role);

        await using var db = pg.CreateContext();
        var tenant = await db.Tenants.SingleAsync();
        var user = await db.Users.SingleAsync();
        Assert.Equal(tenant.Id.ToString("D"), login.TenantId);
        Assert.Equal(user.Id.ToString("D"), login.UserId);
        var device = Assert.Single(await db.Devices.ToListAsync());
        Assert.Equal(DeviceA, device.DeviceKey);
        Assert.Equal("SHOP-PC-01", device.DeviceName);
        Assert.Equal(user.Id, device.UserId);
        Assert.Contains(await db.AuditEvents.ToListAsync(), e => e.EventType == "auth.login.success" && e.UserId == user.Id && e.DeviceId == device.Id);
    }

    [PostgresFact]
    public async Task Tenant_slug_disambiguates_the_same_email_in_two_tenants()
    {
        await using var db = pg.CreateContext();
        var beta = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "beta",
            CreatedAtUtc = _clock.Now,
        };
        var betaUser = new UserEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = beta.Id,
            Email = CloudApiFactory.AdminEmail,
            PasswordHash = "",
            Role = "operator",
            CreatedAtUtc = _clock.Now,
        };
        betaUser.PasswordHash = new PasswordHasher<UserEntity>()
            .HashPassword(betaUser, CloudApiFactory.AdminPassword);
        db.AddRange(beta, betaUser);
        await db.SaveChangesAsync();

        var acmeLogin = await LoginOkAsync();
        var betaResponse = await _client.PostJsonAsync(
            ApiRoutes.AuthLogin,
            Login(device: DeviceB, tenant: "BETA"));
        Assert.Equal(HttpStatusCode.OK, betaResponse.StatusCode);
        var betaLogin = await betaResponse.ReadAsync<LoginResponse>();

        Assert.NotEqual(acmeLogin.TenantId, betaLogin.TenantId);
        Assert.Equal(beta.Id.ToString("D"), betaLogin.TenantId);
        Assert.Equal(betaUser.Id.ToString("D"), betaLogin.UserId);
        Assert.Equal("operator", betaLogin.Role);
    }

    [PostgresFact]
    public async Task Wrong_password_is_rejected_and_audited()
    {
        var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin, Login(password: "nope"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, (await response.ReadErrorAsync()).Code);

        var unknown = await _client.PostJsonAsync(ApiRoutes.AuthLogin, Login(email: "nobody@acme.test"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidCredentials, (await unknown.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.EventType == "auth.login.failed"));
        Assert.Empty(await db.RefreshTokens.ToListAsync());
        Assert.Empty(await db.Devices.ToListAsync());
    }

    [PostgresFact]
    public async Task Malformed_login_is_a_400_invalid_request()
    {
        var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin, Login(device: "not-a-guid"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequest, (await response.ReadErrorAsync()).Code);

        var empty = await _client.PostJsonAsync(ApiRoutes.AuthLogin, new LoginRequest("", "", "", DeviceA, null));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequest, (await empty.ReadErrorAsync()).Code);

        using var malformedBody = new StringContent("{\"email\":", Encoding.UTF8, "application/json");
        using var malformedRequest = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.AuthLogin)
        {
            Content = malformedBody,
        };
        const string malformedCorrelationId = "0198f4c0-4c1b-7a12-8c4d-abcdef123456";
        malformedRequest.Headers.Add(ApiHeaders.CorrelationId, malformedCorrelationId);
        var malformed = await _client.SendAsync(malformedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var error = await malformed.ReadErrorAsync();
        Assert.Equal(ApiErrorCodes.InvalidRequest, error.Code);
        Assert.Equal(malformedCorrelationId, error.CorrelationId);
    }

    [PostgresFact]
    public async Task Same_device_logging_in_twice_is_not_duplicated()
    {
        await LoginOkAsync();
        await LoginOkAsync();
        await LoginOkAsync(DeviceB);

        await using var db = pg.CreateContext();
        var devices = await db.Devices.OrderBy(d => d.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.Equal([DeviceA, DeviceB], devices.Select(d => d.DeviceKey).OrderBy(k => k == DeviceB).ToArray());
        Assert.Equal(3, await db.RefreshTokens.CountAsync());
    }

    [PostgresFact]
    public async Task Two_concurrent_logins_on_the_same_device_both_succeed_without_duplicate_devices()
    {
        var responses = await Task.WhenAll(
            _client.PostJsonAsync(ApiRoutes.AuthLogin, Login()),
            _client.PostJsonAsync(ApiRoutes.AuthLogin, Login()));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        await using var db = pg.CreateContext();
        Assert.Single(await db.Devices.ToListAsync());
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
    }

    [PostgresFact]
    public async Task Access_token_lives_fifteen_minutes_and_carries_the_required_claims()
    {
        var login = await LoginOkAsync();
        var token = new JsonWebTokenHandler().ReadJsonWebToken(login.AccessToken);

        Assert.Equal(login.UserId, token.Subject);
        Assert.Equal(login.TenantId, token.GetClaim("tenant_id").Value);
        Assert.False(string.IsNullOrWhiteSpace(token.GetClaim("device_id").Value));
        Assert.Equal("admin", token.GetClaim("role").Value);
        Assert.True(Guid.TryParse(token.GetClaim("jti").Value, out _));
        Assert.Equal("HS256", token.Alg);
        Assert.Equal("cabinetnc-cloud", token.Issuer);
        Assert.Contains("cabinetnc-desktop", token.Audiences);

        var ttl = token.ValidTo - token.IssuedAt;
        Assert.InRange(ttl, TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(2));
        Assert.InRange(token.IssuedAt, _clock.Now.UtcDateTime.AddSeconds(-2), _clock.Now.UtcDateTime.AddSeconds(2));

        await using var db = pg.CreateContext();
        var device = await db.Devices.SingleAsync();
        Assert.Equal(device.Id.ToString("D"), token.GetClaim("device_id").Value);
    }

    [PostgresFact]
    public async Task Refresh_rotates_the_refresh_token_and_issues_a_new_access_token()
    {
        var login = await LoginOkAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));

        var response = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken, DeviceA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertTokenResponseIsNotCacheable(response);
        var refreshed = await response.ReadAsync<RefreshResponse>();
        Assert.NotEqual(login.AccessToken, refreshed.AccessToken);
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(15 * 60, refreshed.AccessTokenExpiresInSeconds);
        Assert.Equal(30 * 24 * 3600, refreshed.RefreshTokenExpiresInSeconds);

        await using var db = pg.CreateContext();
        var tokens = await db.RefreshTokens.OrderBy(t => t.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, tokens.Count);
        var (old, current) = (tokens[0], tokens[1]);
        Assert.Equal(_clock.Now, old.RevokedAtUtc);
        Assert.Equal(current.Id, old.ReplacedByTokenId);
        Assert.Null(current.RevokedAtUtc);
        Assert.Equal(old.FamilyId, current.FamilyId);
        Assert.Equal(old.DeviceId, current.DeviceId);
        Assert.Equal(_clock.Now.AddDays(30), current.ExpiresAtUtc);
        Assert.Contains(await db.AuditEvents.ToListAsync(), e => e.EventType == "auth.refresh.success");
    }

    [PostgresFact]
    public async Task Reusing_a_rotated_refresh_token_is_detected_and_revokes_the_whole_family()
    {
        var login = await LoginOkAsync();
        var first = await (await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken, DeviceA))).ReadAsync<RefreshResponse>();

        var reuse = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken, DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshReuseDetected, (await reuse.ReadErrorAsync()).Code);

        // The legitimate successor is collateral: the family is dead and the device must log in again.
        var successor = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(first.RefreshToken, DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await successor.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.All(await db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAtUtc));
        Assert.Contains(await db.AuditEvents.ToListAsync(), e => e.EventType == "auth.refresh.failed" && (e.DetailsJson ?? "").Contains("reuse"));
    }

    [PostgresFact]
    public async Task Two_concurrent_refreshes_cannot_both_succeed()
    {
        var login = await LoginOkAsync();
        var request = new RefreshRequest(login.RefreshToken, DeviceA);

        var responses = await Task.WhenAll(
            _client.PostJsonAsync(ApiRoutes.AuthRefresh, request),
            _client.PostJsonAsync(ApiRoutes.AuthRefresh, request));

        var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var reuse = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
        Assert.Equal(ApiErrorCodes.RefreshReuseDetected, (await reuse.ReadErrorAsync()).Code);

        // The second request detected reuse only after waiting on the row lock; it revoked the
        // successor emitted by the winner, so the client has to log in again.
        var rotated = await success.ReadAsync<RefreshResponse>();
        var successor = await _client.PostJsonAsync(
            ApiRoutes.AuthRefresh,
            new RefreshRequest(rotated.RefreshToken, DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, successor.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await successor.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        Assert.All(await db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAtUtc));
    }

    [PostgresFact]
    public async Task Refresh_waits_on_the_token_family_advisory_lock()
    {
        var login = await LoginOkAsync();
        await using var lockDb = pg.CreateContext();
        var familyId = await lockDb.RefreshTokens.Select(token => token.FamilyId).SingleAsync();
        await using var lockTransaction = await lockDb.Database.BeginTransactionAsync();
        var lockKey = AuthService.FamilyLockKey(familyId);
        await lockDb.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})");

        var refreshTask = _client.PostJsonAsync(
            ApiRoutes.AuthRefresh,
            new RefreshRequest(login.RefreshToken, DeviceA));

        await using var observer = pg.CreateContext();
        var waiterObserved = false;
        for (var i = 0; i < 50 && !waiterObserved; i++)
        {
            waiterObserved = await observer.Database.SqlQueryRaw<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted) AS \"Value\"")
                .SingleAsync();
            if (!waiterObserved)
                await Task.Delay(100);
        }
        Assert.True(waiterObserved, "the refresh request never waited on the family advisory lock");
        Assert.False(refreshTask.IsCompleted);

        await lockTransaction.CommitAsync();
        var response = await refreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [PostgresFact]
    public async Task Old_token_reuse_racing_a_successor_refresh_leaves_no_active_family_member()
    {
        var login = await LoginOkAsync();
        var rotated = await (await _client.PostJsonAsync(
            ApiRoutes.AuthRefresh,
            new RefreshRequest(login.RefreshToken, DeviceA))).ReadAsync<RefreshResponse>();

        var responses = await Task.WhenAll(
            _client.PostJsonAsync(
                ApiRoutes.AuthRefresh,
                new RefreshRequest(login.RefreshToken, DeviceA)),
            _client.PostJsonAsync(
                ApiRoutes.AuthRefresh,
                new RefreshRequest(rotated.RefreshToken, DeviceA)));

        var unauthorized = responses
            .Where(response => response.StatusCode == HttpStatusCode.Unauthorized)
            .ToArray();
        Assert.InRange(unauthorized.Length, 1, 2);
        var errors = await Task.WhenAll(unauthorized.Select(response => response.ReadErrorAsync()));
        Assert.Contains(errors, error => error.Code == ApiErrorCodes.RefreshReuseDetected);

        var winner = responses.SingleOrDefault(response => response.StatusCode == HttpStatusCode.OK);
        if (winner is not null)
        {
            var shortLivedSuccessor = await winner.ReadAsync<RefreshResponse>();
            var afterReuse = await _client.PostJsonAsync(
                ApiRoutes.AuthRefresh,
                new RefreshRequest(shortLivedSuccessor.RefreshToken, DeviceA));
            Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
            Assert.Equal(ApiErrorCodes.RefreshInvalid, (await afterReuse.ReadErrorAsync()).Code);
        }

        await using var db = pg.CreateContext();
        Assert.All(await db.RefreshTokens.ToListAsync(), token => Assert.NotNull(token.RevokedAtUtc));
    }

    [PostgresFact]
    public async Task Unknown_expired_or_foreign_device_refresh_tokens_are_rejected()
    {
        var login = await LoginOkAsync();

        var unknown = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest("not-a-real-token", DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await unknown.ReadErrorAsync()).Code);

        var otherDevice = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken, DeviceB));
        Assert.Equal(HttpStatusCode.Unauthorized, otherDevice.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidDevice, (await otherDevice.ReadErrorAsync()).Code);

        var again = await LoginOkAsync();
        _clock.Advance(TimeSpan.FromDays(31));
        var expired = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(again.RefreshToken, DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await expired.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Refresh_rejects_an_internally_inconsistent_token_user_device_tenant()
    {
        var login = await LoginOkAsync();
        await using (var corrupt = pg.CreateContext())
        {
            var device = await corrupt.Devices.SingleAsync();
            device.TenantId = Guid.NewGuid();
            await corrupt.SaveChangesAsync();
        }

        var response = await _client.PostJsonAsync(
            ApiRoutes.AuthRefresh,
            new RefreshRequest(login.RefreshToken, DeviceA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await response.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Logout_revokes_the_refresh_token_family()
    {
        var login = await LoginOkAsync();

        var logout = await _client.PostJsonAsync(ApiRoutes.AuthLogout, new LogoutRequest(login.RefreshToken, DeviceA), bearer: login.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await _client.PostJsonAsync(ApiRoutes.AuthRefresh, new RefreshRequest(login.RefreshToken, DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(ApiErrorCodes.RefreshInvalid, (await refresh.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.All(await db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAtUtc));
        Assert.Contains(await db.AuditEvents.ToListAsync(), e => e.EventType == "auth.logout");
    }

    [PostgresFact]
    public async Task Protected_endpoint_without_token_or_with_expired_token_returns_the_api_error_shape()
    {
        var anonymous = await _client.PostJsonAsync(ApiRoutes.AuthLogout, new LogoutRequest("x", DeviceA));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Bearer", Assert.Single(anonymous.Headers.WwwAuthenticate).Scheme);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await anonymous.ReadErrorAsync()).Code);

        // Issue a token an hour in the past; by wall-clock time it expired 45 minutes ago.
        _clock.Advance(TimeSpan.FromHours(-1));
        var stale = await LoginOkAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        var expired = await _client.PostJsonAsync(ApiRoutes.AuthLogout, new LogoutRequest(stale.RefreshToken, DeviceA), bearer: stale.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Contains("invalid_token", Assert.Single(expired.Headers.WwwAuthenticate).Parameter);
        Assert.Equal(ApiErrorCodes.TokenExpired, (await expired.ReadErrorAsync()).Code);

        var garbage = await _client.PostJsonAsync(ApiRoutes.AuthLogout, new LogoutRequest("x", DeviceA), bearer: "not.a.jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await garbage.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Raw_refresh_token_is_never_stored_only_its_sha256()
    {
        var login = await LoginOkAsync();

        // A hostile client may try to smuggle the token into the persisted correlation-id column.
        // Only canonical UUIDs are accepted, so this Base64Url value must be replaced.
        var smuggle = await _client.PostJsonAsync(
            ApiRoutes.AuthLogin,
            Login(password: "wrong"),
            correlationId: login.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, smuggle.StatusCode);
        var safeCorrelation = Assert.Single(smuggle.Headers.GetValues(ApiHeaders.CorrelationId));
        Assert.NotEqual(login.RefreshToken, safeCorrelation);
        Assert.True(Guid.TryParseExact(safeCorrelation, "D", out _));

        await using var db = pg.CreateContext();
        var stored = Assert.Single(await db.RefreshTokens.ToListAsync());
        Assert.Equal(Sha256Hex(login.RefreshToken), stored.TokenHash);
        Assert.NotEqual(login.RefreshToken, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);

        // Nothing anywhere in the database contains the raw token.
        var everything = string.Join("\n", await db.Database.SqlQueryRaw<string>(
            """
            SELECT to_jsonb(row_data)::text AS "Value" FROM "Tenants" row_data
            UNION ALL SELECT to_jsonb(row_data)::text FROM "Users" row_data
            UNION ALL SELECT to_jsonb(row_data)::text FROM "Devices" row_data
            UNION ALL SELECT to_jsonb(row_data)::text FROM "RefreshTokens" row_data
            UNION ALL SELECT to_jsonb(row_data)::text FROM "ComputeJobs" row_data
            UNION ALL SELECT to_jsonb(row_data)::text FROM "AuditEvents" row_data
            """).ToListAsync());
        Assert.DoesNotContain(login.RefreshToken, everything);
    }

    [PostgresFact]
    public async Task Login_is_rate_limited_per_client()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin, Login(password: "wrong"));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Equal(ApiErrorCodes.RateLimited, (await response.ReadErrorAsync()).Code);
                Assert.Single(response.Headers.GetValues(ApiHeaders.CorrelationId));
                Assert.NotNull(response.Headers.RetryAfter);
            }
        }

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [PostgresFact]
    public async Task Refresh_is_rate_limited_per_client()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            var response = await _client.PostJsonAsync(
                ApiRoutes.AuthRefresh,
                new RefreshRequest("unknown-" + i, DeviceA));
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                Assert.Equal(ApiErrorCodes.RateLimited, (await response.ReadErrorAsync()).Code);
        }

        Assert.Equal(30, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }
}
