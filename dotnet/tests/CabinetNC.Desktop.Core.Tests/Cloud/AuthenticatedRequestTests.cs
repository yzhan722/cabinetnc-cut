using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

/// <summary>The bearer pipeline: attach token, on 401 refresh exactly once and replay the request once.</summary>
public class AuthenticatedRequestTests
{
    readonly FakeCloudApiHandler _api = new();
    readonly InMemoryTokenStore _tokens = new();
    readonly TestClock _clock = new();
    readonly TempDirectory _dir = new();
    static readonly CancellationToken CT = CancellationToken.None;

    static readonly SubmitNestJobRequest Request = new(
        [new NestPartDto("A", 600, 400, true, "MDF", 18)], 1220, 2440, 12, 15, true);

    async Task<CloudApiClient> LoggedInClientAsync()
    {
        var client = ClientFixtures.Client(_api, _tokens, _dir, _clock);
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        return client;
    }

    [Fact]
    public async Task Requests_carry_the_current_access_token()
    {
        using var client = await LoggedInClientAsync();

        var response = await client.SubmitNestJobAsync(Request, "k1", CT);

        Assert.Equal(JobStatus.Queued, response.Status);
        Assert.Equal(["k1"], _api.SubmittedIdempotencyKeys);
        Assert.Equal(0, _api.RefreshCount);
    }

    [Fact]
    public async Task Unauthenticated_client_fails_fast_without_touching_the_network()
    {
        using var client = ClientFixtures.Client(_api, _tokens, _dir, _clock);

        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => client.SubmitNestJobAsync(Request, "k1", CT));

        Assert.DoesNotContain(_api.RequestLog, r => r.Contains("/jobs/"));
    }

    [Fact]
    public async Task Server_side_expiry_triggers_one_refresh_and_a_replayed_request()
    {
        using var client = await LoggedInClientAsync();
        _api.ExpireAccessToken("access-1");

        var response = await client.SubmitNestJobAsync(Request, "k2", CT);

        Assert.Equal(JobStatus.Queued, response.Status);
        Assert.Equal(1, _api.RefreshCount);
        Assert.Equal(2, _api.RequestLog.Count(r => r == $"POST {ApiRoutes.JobsNest}"));
        Assert.Equal(["k2"], _api.SubmittedIdempotencyKeys);
        Assert.Equal("access-2", await client.Session.GetAccessTokenAsync(CT));
    }

    [Fact]
    public async Task Concurrent_expired_requests_share_one_refresh()
    {
        using var client = await LoggedInClientAsync();
        _api.ExpireAccessToken("access-1");

        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.SubmitNestJobAsync(Request, $"k{i}", CT)));

        Assert.All(statuses, s => Assert.Equal(JobStatus.Queued, s.Status));
        Assert.Equal(1, _api.RefreshCount);
    }

    [Fact]
    public async Task A_second_401_is_not_retried_again()
    {
        using var client = await LoggedInClientAsync();
        _api.ExpireAccessToken("access-1");
        _api.RejectRefresh = true;

        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => client.SubmitNestJobAsync(Request, "k3", CT));

        Assert.Equal(1, _api.RefreshCount);
        Assert.Equal(1, _api.RequestLog.Count(r => r == $"POST {ApiRoutes.JobsNest}"));
        Assert.Empty(_api.SubmittedIdempotencyKeys);
        Assert.False(client.Session.IsAuthenticated);
    }

    [Fact]
    public async Task Non_auth_errors_are_surfaced_as_api_exceptions_with_the_error_code()
    {
        using var client = await LoggedInClientAsync();

        var error = await Assert.ThrowsAsync<CloudApiException>(() => client.GetJobResultAsync(Guid.NewGuid(), CT));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotFound, error.Code);
        Assert.Equal("corr-fake", error.Error?.CorrelationId);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        using var client = ClientFixtures.Client(_api, _tokens, _dir, _clock);

        var health = await client.GetHealthAsync(CT);

        Assert.Equal("ok", health.Status);
    }
}
