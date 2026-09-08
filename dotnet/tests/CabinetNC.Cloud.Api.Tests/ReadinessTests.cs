using System.Net;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Tests;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>Operators and the proxy need a truthful readiness signal, not a static "ok".</summary>
public class ReadinessTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    readonly ManualClock _clock = new() { Now = DateTimeOffset.UtcNow };
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

    [PostgresFact]
    public async Task Ready_when_database_and_object_store_answer()
    {
        var response = await _client.GetAsync(ApiRoutes.HealthReady);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ready = await response.ReadAsync<ReadinessResponse>();
        Assert.Equal("ready", ready.Status);
        Assert.Equal("ok", ready.Database.Status);
        Assert.Equal("ok", ready.ObjectStore.Status);
        Assert.Equal(0, ready.QueuedJobs);
        Assert.Equal(0, ready.RunningJobs);
    }

    [PostgresFact]
    public async Task Not_ready_with_503_when_the_object_store_fails_and_no_secrets_leak()
    {
        _factory.ObjectStore.FailReadWhen = _ => true;

        var response = await _client.GetAsync(ApiRoutes.HealthReady);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var ready = await response.ReadAsync<ReadinessResponse>();
        Assert.Equal("not_ready", ready.Status);
        Assert.Equal("ok", ready.Database.Status);
        Assert.Equal("failed", ready.ObjectStore.Status);
        Assert.Equal("IOException", ready.ObjectStore.Reason);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", json);
    }

    [PostgresFact]
    public async Task Liveness_stays_static_and_anonymous()
    {
        var response = await _client.GetAsync(ApiRoutes.Health);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.ReadAsync<HealthResponse>()).Status);
    }
}
