using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

public class AuthSessionTests
{
    readonly FakeCloudApiHandler _api = new();
    readonly InMemoryTokenStore _tokens = new();
    readonly TestClock _clock = new();
    readonly TempDirectory _dir = new();
    static readonly CancellationToken CT = CancellationToken.None;

    CloudApiClient Client() => ClientFixtures.Client(_api, _tokens, _dir, _clock);

    [Fact]
    public async Task Login_stores_identity_in_memory_and_only_the_refresh_token_on_disk()
    {
        using var client = Client();

        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);

        Assert.True(client.Session.IsAuthenticated);
        Assert.Equal("op@example.internal", client.Session.Email);
        Assert.Equal(FakeCloudApiHandler.TenantId, client.Session.TenantId);
        Assert.Equal("operator", client.Session.Role);
        Assert.Equal(_clock.Now.AddSeconds(900), client.Session.AccessTokenExpiresAtUtc);
        Assert.Equal("access-1", await client.Session.GetAccessTokenAsync(CT));

        Assert.NotNull(_tokens.Stored);
        Assert.Equal("refresh-1", _tokens.Stored!.RefreshToken);
        Assert.Equal("shop", _tokens.Stored.Tenant);
        Assert.Equal(new DeviceIdentityStore(_dir.Path).GetOrCreate().ToString("D"), _tokens.Stored.DeviceId);
        Assert.Single(_api.LoginDeviceIds);
        Assert.Equal(_tokens.Stored.DeviceId, _api.LoginDeviceIds[0]);
    }

    [Fact]
    public async Task Wrong_password_surfaces_the_api_error_and_leaves_the_session_signed_out()
    {
        using var client = Client();

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.Session.LoginAsync("op@example.internal", "nope", CT));

        Assert.Equal(ApiErrorCodes.InvalidCredentials, error.Code);
        Assert.False(client.Session.IsAuthenticated);
        Assert.Null(_tokens.Stored);
        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => client.Session.GetAccessTokenAsync(CT));
    }

    [Fact]
    public async Task Access_token_is_reused_until_the_last_minute_then_refreshed_once()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);

        _clock.Advance(TimeSpan.FromSeconds(900 - 61));
        Assert.Equal("access-1", await client.Session.GetAccessTokenAsync(CT));
        Assert.Equal(0, _api.RefreshCount);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("access-2", await client.Session.GetAccessTokenAsync(CT));
        Assert.Equal(1, _api.RefreshCount);
        Assert.Equal("refresh-2", _tokens.Stored!.RefreshToken);
        Assert.Equal(_clock.Now.AddSeconds(900), client.Session.AccessTokenExpiresAtUtc);

        Assert.Equal("access-2", await client.Session.GetAccessTokenAsync(CT));
        Assert.Equal(1, _api.RefreshCount);
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_refresh()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        _clock.Advance(TimeSpan.FromSeconds(900));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.Session.GetAccessTokenAsync(CT)));

        Assert.All(tokens, t => Assert.Equal("access-2", t));
        Assert.Equal(1, _api.RefreshCount);
    }

    [Fact]
    public async Task Force_refresh_with_a_stale_token_does_not_refresh_again()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);

        var first = await client.Session.ForceRefreshAsync("access-1", CT);
        var second = await client.Session.ForceRefreshAsync("access-1", CT);

        Assert.Equal("access-2", first);
        Assert.Equal("access-2", second);
        Assert.Equal(1, _api.RefreshCount);
    }

    [Fact]
    public async Task Rejected_refresh_signs_out_and_wipes_the_stored_token()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        _api.RejectRefresh = true;
        _clock.Advance(TimeSpan.FromSeconds(900));

        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => client.Session.GetAccessTokenAsync(CT));

        Assert.False(client.Session.IsAuthenticated);
        Assert.Null(_tokens.Stored);
        Assert.Equal(1, _tokens.ClearCount);
    }

    [Fact]
    public async Task Network_failure_during_refresh_keeps_the_session_for_a_later_retry()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        _clock.Advance(TimeSpan.FromSeconds(900));
        _api.NetworkFailuresRemaining = 1;

        await Assert.ThrowsAsync<ComputeUnavailableException>(() => client.Session.GetAccessTokenAsync(CT));

        Assert.True(client.Session.IsAuthenticated);
        Assert.Equal("refresh-1", _tokens.Stored!.RefreshToken);
        Assert.Equal("access-2", await client.Session.GetAccessTokenAsync(CT));
    }

    [Fact]
    public async Task Restore_from_disk_refreshes_immediately_and_restores_identity()
    {
        var deviceId = new DeviceIdentityStore(_dir.Path).GetOrCreate().ToString("D");
        using (var first = Client())
            await first.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);

        using var second = Client();
        Assert.False(second.Session.IsAuthenticated);
        var restored = await second.Session.TryRestoreAsync(CT);

        Assert.True(restored);
        Assert.True(second.Session.IsAuthenticated);
        Assert.Equal("op@example.internal", second.Session.Email);
        Assert.Equal("access-2", await second.Session.GetAccessTokenAsync(CT));
        Assert.Equal("refresh-2", _tokens.Stored!.RefreshToken);
        Assert.Equal(deviceId, _tokens.Stored.DeviceId);
        Assert.Equal(1, _api.LoginCount);
    }

    [Fact]
    public async Task Restore_fails_cleanly_when_nothing_is_stored_or_the_token_is_dead()
    {
        using var client = Client();
        Assert.False(await client.Session.TryRestoreAsync(CT));

        var deviceId = new DeviceIdentityStore(_dir.Path).GetOrCreate().ToString("D");
        _tokens.Save(new StoredRefreshToken("refresh-unknown", deviceId, "shop", "op@example.internal", _clock.Now.AddDays(30)));
        Assert.False(await client.Session.TryRestoreAsync(CT));
        Assert.Equal(1, _api.RefreshCount);
        Assert.False(client.Session.IsAuthenticated);
        Assert.Null(_tokens.Stored);
    }

    [Fact]
    public async Task Restore_ignores_tokens_for_another_tenant_or_device()
    {
        using var client = Client();
        _tokens.Save(new StoredRefreshToken("refresh-x", "some-other-device", "shop", "op@example.internal", _clock.Now.AddDays(30)));

        Assert.False(await client.Session.TryRestoreAsync(CT));
        Assert.Equal(0, _api.RefreshCount);
    }

    [Fact]
    public async Task Logout_revokes_server_side_and_forgets_everything_locally()
    {
        using var client = Client();
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        var changes = 0;
        client.Session.Changed += () => changes++;

        await client.Session.LogoutAsync(CT);

        Assert.Equal(1, _api.LogoutCount);
        Assert.False(client.Session.IsAuthenticated);
        Assert.Null(_tokens.Stored);
        Assert.True(changes >= 1);
        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => client.Session.GetAccessTokenAsync(CT));
    }
}
