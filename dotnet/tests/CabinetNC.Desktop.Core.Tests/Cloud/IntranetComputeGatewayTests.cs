using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

/// <summary>submit → JobId → poll (1s, 2s, … max 5s) → result; never a long-blocking request.</summary>
public class IntranetComputeGatewayTests
{
    readonly FakeCloudApiHandler _api = new();
    readonly InMemoryTokenStore _tokens = new();
    readonly TestClock _clock = new();
    readonly TempDirectory _dir = new();
    readonly List<TimeSpan> _delays = [];
    static readonly CancellationToken CT = CancellationToken.None;

    static readonly SubmitNestJobRequest Request = new(
        [new NestPartDto("A", 600, 400, true, "MDF", 18), new NestPartDto("B", 600, 400, true, "MDF", 18)],
        1220, 2440, 12, 15, true);

    async Task<(CloudApiClient Client, IntranetComputeGateway Gateway)> GatewayAsync(CloudClientOptions? options = null)
    {
        var client = new CloudApiClient(options ?? ClientFixtures.Options(), _tokens, new DeviceIdentityStore(_dir.Path), _clock, _api);
        await client.Session.LoginAsync("op@example.internal", FakeCloudApiHandler.Password, CT);
        var gateway = new IntranetComputeGateway(client, client.Options, _clock, (delay, _) =>
        {
            _delays.Add(delay);
            _clock.Advance(delay);
            return Task.CompletedTask;
        });
        return (client, gateway);
    }

    [Fact]
    public async Task Submits_polls_with_backoff_and_returns_the_result()
    {
        _api.StatusSequence = [JobStatus.Queued, JobStatus.Queued, JobStatus.Running, JobStatus.Running, JobStatus.Running, JobStatus.Succeeded];
        var (client, gateway) = await GatewayAsync();
        using var _ = client;
        var progress = new List<ComputeProgress>();

        var result = await gateway.RunNestingAsync(Request, new SyncProgress<ComputeProgress>(progress.Add), CT);

        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal(["A", "B"], result.Placements.Select(p => p.PanelId));
        Assert.Equal(["BIG"], result.Unplaced);
        Assert.Equal(42, result.DurationMs);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)],
            _delays);
        Assert.Single(_api.SubmittedIdempotencyKeys);
        Assert.Contains(progress, p => p.Status == JobStatus.Queued);
        Assert.Contains(progress, p => p.Status == JobStatus.Running);
        Assert.Contains(progress, p => p.Status == JobStatus.Succeeded);
        Assert.All(progress, p => Assert.Equal(result.JobId, p.JobId));
    }

    [Fact]
    public async Task Each_run_uses_a_fresh_idempotency_key_so_identical_inputs_are_computed_again()
    {
        var (client, gateway) = await GatewayAsync();
        using var _ = client;

        var first = await gateway.RunNestingAsync(Request, null, CT);
        var second = await gateway.RunNestingAsync(Request, null, CT);

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(2, _api.SubmittedIdempotencyKeys.Distinct().Count());
    }

    [Fact]
    public async Task Failed_job_raises_the_server_error_code_and_message()
    {
        _api.StatusSequence = [JobStatus.Running, JobStatus.Failed];
        _api.FailedJobErrorCode = ApiErrorCodes.StorageFailed;
        var (client, gateway) = await GatewayAsync();
        using var _ = client;

        var error = await Assert.ThrowsAsync<ComputeJobFailedException>(() => gateway.RunNestingAsync(Request, null, CT));

        Assert.Equal(ApiErrorCodes.StorageFailed, error.ErrorCode);
        Assert.Contains("engine said no", error.Message);
        Assert.NotEqual(Guid.Empty, error.JobId);
    }

    [Fact]
    public async Task Gives_up_after_the_job_timeout_without_blocking_forever()
    {
        _api.StatusSequence = [JobStatus.Running];
        var options = ClientFixtures.Options();
        options.JobTimeout = TimeSpan.FromSeconds(30);
        var (client, gateway) = await GatewayAsync(options);
        using var _ = client;

        var error = await Assert.ThrowsAsync<ComputeJobTimeoutException>(() => gateway.RunNestingAsync(Request, null, CT));

        Assert.NotEqual(Guid.Empty, error.JobId);
        Assert.True(_delays.Sum(d => d.TotalSeconds) >= 30);
        Assert.All(_delays, d => Assert.InRange(d.TotalSeconds, 1, 5));
    }

    [Fact]
    public async Task Network_loss_while_polling_is_retried_and_the_job_is_still_collected()
    {
        _api.StatusSequence = [JobStatus.Running, JobStatus.Succeeded];
        var (client, gateway) = await GatewayAsync();
        using var _ = client;
        _api.NetworkFailureFilter = r => r.Method == HttpMethod.Get;
        _api.NetworkFailuresRemaining = 3;

        var result = await gateway.RunNestingAsync(Request, null, CT);

        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal(0, _api.NetworkFailuresRemaining);
        Assert.Single(_api.SubmittedIdempotencyKeys);
    }

    [Fact]
    public async Task Network_loss_during_submit_is_retried_with_the_same_idempotency_key()
    {
        var (client, gateway) = await GatewayAsync();
        using var _ = client;
        _api.NetworkFailureFilter = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == ApiRoutes.JobsNest;
        _api.NetworkFailuresRemaining = 2;

        var result = await gateway.RunNestingAsync(Request, null, CT);

        Assert.NotEqual(Guid.Empty, result.JobId);
        Assert.Contains(_api.RequestLog, r => r == $"POST {ApiRoutes.JobsNest}");
        Assert.Single(_api.SubmittedIdempotencyKeys.Distinct());
    }

    [Fact]
    public async Task Prolonged_outage_ends_with_an_unavailable_error_not_a_hang()
    {
        var options = ClientFixtures.Options();
        options.NetworkGrace = TimeSpan.FromSeconds(10);
        var (client, gateway) = await GatewayAsync(options);
        using var _ = client;
        _api.NetworkFailuresRemaining = 1000;

        await Assert.ThrowsAsync<ComputeUnavailableException>(() => gateway.RunNestingAsync(Request, null, CT));

        Assert.True(_api.NetworkFailuresRemaining > 900, "must stop retrying once the grace period is exhausted");
    }

    [Fact]
    public async Task Cancellation_stops_polling_promptly()
    {
        _api.StatusSequence = [JobStatus.Running];
        var (client, gateway) = await GatewayAsync();
        using var _ = client;
        using var cts = new CancellationTokenSource();
        var polls = 0;
        var cancellingGateway = new IntranetComputeGateway(client, client.Options, _clock, (delay, ct) =>
        {
            if (++polls == 3) cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellingGateway.RunNestingAsync(Request, null, cts.Token));

        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task V2_request_round_trips_through_the_same_polling_loop_and_maps_to_local_shapes()
    {
        var (client, gateway) = await GatewayAsync();
        using var _ = client;
        var (request, error) = NestRequestBuilder.BuildV2(
            [
                new CabinetNC.Domain.Parts.Panel { PanelId = "L", Material = "MDF", ThicknessMm = 18, Outline = new CabinetNC.Domain.Geometry.Outline { Points = [new(0, 0), new(700, 0), new(700, 250), new(300, 250), new(300, 500), new(0, 500)] } },
                new CabinetNC.Domain.Parts.Panel { PanelId = "R", Material = "MDF", ThicknessMm = 18, Outline = new CabinetNC.Domain.Geometry.Outline { Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)] } },
            ],
            new CabinetNC.Domain.Nesting.NestSettings { MarginMm = 15, ClearanceMm = 12 },
            [new CabinetNC.Domain.Nesting.NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, Label = "full", Material = "MDF", ThicknessMm = 18 }],
            "nfp",
            TimeSpan.FromSeconds(25));
        Assert.Null(error);
        Assert.Equal(6, request!.Panels[0].Outline.Count);   // true shape, not a bounding box

        var result = await gateway.RunNestingV2Async(request, null, CT);

        Assert.Equal("clipper_nfp_v1", result.Result.Engine);
        Assert.Contains(_api.RequestLog, r => r == $"POST {ApiRoutes.JobsNestV2}");
        var (local, log) = NestResultMapper.ToLocal(result);
        Assert.Equal(2, local.SheetCount);
        Assert.Equal(2, local.SheetsUsed.Count);
        Assert.Equal("full", local.SheetsUsed[0].Label);
        Assert.Equal(["BIG"], local.Unplaced);
        Assert.Equal("too_large", Assert.Single(local.UnplacedReasons).Code);
        Assert.Equal(41.5, Assert.Single(local.GroupReports).UtilizationPct);
        Assert.Equal(87, log.ElapsedMs);
        Assert.Equal("clipper_nfp_v1", log.SelectedEngine);
    }

    [Fact]
    public void V2_builder_reports_contract_violations_instead_of_downgrading()
    {
        var (request, error) = NestRequestBuilder.BuildV2(
            [new CabinetNC.Domain.Parts.Panel { PanelId = "", Material = "MDF", ThicknessMm = 18, Outline = new CabinetNC.Domain.Geometry.Outline { Points = [new(0, 0), new(1, 0), new(1, 1)] } }],
            new CabinetNC.Domain.Nesting.NestSettings(),
            [],
            "nfp",
            TimeSpan.FromSeconds(25));

        Assert.Null(request);
        Assert.Contains("panelId", error);
    }

    [Fact]
    public async Task Signed_out_session_refuses_to_run_instead_of_falling_back()
    {
        var client = new CloudApiClient(ClientFixtures.Options(), _tokens, new DeviceIdentityStore(_dir.Path), _clock, _api);
        using var _ = client;
        var gateway = new IntranetComputeGateway(client, client.Options, _clock, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<CloudAuthenticationRequiredException>(() => gateway.RunNestingAsync(Request, null, CT));
        Assert.Empty(_api.SubmittedIdempotencyKeys);
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
