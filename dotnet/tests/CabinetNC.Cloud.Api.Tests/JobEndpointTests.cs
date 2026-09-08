using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Api.Jobs;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Tests;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Cloud.Worker;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CabinetNC.Cloud.Api.Tests;

public class JobEndpointTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    const string DeviceKey = "51bbf310-f67b-4f81-bb50-847a62bbd8b7";

    readonly ManualClock _clock = new() { Now = TruncateToSeconds(DateTimeOffset.UtcNow) };
    CloudApiFactory _factory = null!;
    HttpClient _client = null!;
    LoginResponse _login = null!;

    static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;
        await pg.ResetAsync();
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
        _client = _factory.CreateClient();
        var response = await _client.PostJsonAsync(
            ApiRoutes.AuthLogin,
            new LoginRequest(
                CloudApiFactory.Tenant,
                CloudApiFactory.AdminEmail,
                CloudApiFactory.AdminPassword,
                DeviceKey,
                "JOB-TEST-PC"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _login = await response.ReadAsync<LoginResponse>();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    static SubmitNestJobRequest ValidRequest(double widthA = 600) =>
        new(
            Parts:
            [
                new NestPartDto("A", widthA, 400, true, "MDF", 18),
                new NestPartDto("B", 350, 250, false, "MDF", 18),
            ],
            SheetWidthMm: 1220,
            SheetLengthMm: 2440,
            SpacingMm: 12,
            BorderMm: 15,
            AllowRotation: true);

    async Task<HttpResponseMessage> SubmitAsync(
        SubmitNestJobRequest body,
        string? idempotencyKey,
        string? bearer = null,
        string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.JobsNest)
        {
            Content = new StringContent(CloudJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, idempotencyKey);
        if (correlationId is not null)
            request.Headers.TryAddWithoutValidation(ApiHeaders.CorrelationId, correlationId);
        var token = bearer ?? _login?.AccessToken;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    async Task<HttpResponseMessage> GetAsAsync(string route, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _client.SendAsync(request);
    }

    [PostgresFact]
    public async Task Submit_requires_authentication()
    {
        var response = await SubmitAsync(ValidRequest(), "unauth-1", bearer: "");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await response.ReadErrorAsync()).Code);
        await using var db = pg.CreateContext();
        Assert.Empty(await db.ComputeJobs.ToListAsync());
    }

    [PostgresFact]
    public async Task Submit_requires_one_nonempty_idempotency_key()
    {
        var missing = await SubmitAsync(ValidRequest(), idempotencyKey: null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequest, (await missing.ReadErrorAsync()).Code);

        var blank = await SubmitAsync(ValidRequest(), idempotencyKey: " ");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(ApiErrorCodes.InvalidRequest, (await blank.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Submit_rejects_invalid_or_oversized_requests()
    {
        var valid = ValidRequest();
        var cases = new[]
        {
            valid with { Parts = [] },
            valid with { Parts = [new NestPartDto("", 1, 1, true, null, 0)] },
            valid with { Parts = [new NestPartDto("DUP", 1, 1, true, null, 0), new NestPartDto("DUP", 2, 2, true, null, 0)] },
            valid with { Parts = [new NestPartDto("ZERO", 0, 1, true, null, 0)] },
            valid with { Parts = [new NestPartDto("NEG-T", 1, 1, true, null, -1)] },
            valid with { SheetWidthMm = 0 },
            valid with { SheetLengthMm = -1 },
            valid with { SpacingMm = -0.1 },
            valid with { BorderMm = -0.1 },
            valid with
            {
                Parts = Enumerable.Range(0, NestJobValidator.MaxParts + 1)
                    .Select(i => new NestPartDto($"P{i}", 1, 1, true, null, 0))
                    .ToArray(),
            },
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var response = await SubmitAsync(cases[i], $"invalid-{i}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ApiErrorCodes.InvalidRequest, (await response.ReadErrorAsync()).Code);
        }

        await using var db = pg.CreateContext();
        Assert.Empty(await db.ComputeJobs.ToListAsync());
    }

    [PostgresFact]
    public async Task Submit_stores_canonical_input_hash_then_marks_the_job_claimable()
    {
        const string correlationId = "0198f4c0-4c1b-7a12-8c4d-999999999999";
        var body = ValidRequest();
        var expectedBytes = Encoding.UTF8.GetBytes(CloudJson.Serialize(body));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));

        var response = await SubmitAsync(body, "store-1", correlationId: correlationId);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<SubmitNestJobResponse>();
        Assert.Equal(JobStatus.Queued, accepted.Status);
        Assert.Equal(correlationId, accepted.CorrelationId);

        await using var db = pg.CreateContext();
        var job = await db.ComputeJobs.SingleAsync();
        Assert.Equal(accepted.JobId, job.Id);
        Assert.Equal(Guid.Parse(_login.TenantId), job.TenantId);
        Assert.Equal(Guid.Parse(_login.UserId), job.UserId);
        Assert.Equal(expectedHash, job.InputSha256);
        Assert.Equal(ObjectKeys.JobInput(job.TenantId, job.Id), job.InputObjectKey);
        Assert.Equal(_clock.Now, job.InputStoredAtUtc);
        Assert.Equal(correlationId, job.CorrelationId);
        var device = await db.Devices.SingleAsync();
        Assert.Equal(device.Id, job.DeviceId);
        Assert.Contains(await db.AuditEvents.ToListAsync(), audit =>
            audit.EventType == "job.submitted" && audit.JobId == job.Id);

        var stored = _factory.ObjectStore.Objects[job.InputObjectKey];
        Assert.Equal("application/json", stored.ContentType);
        Assert.Equal(expectedBytes, stored.Bytes);

        await using var claimDb = pg.CreateContext();
        var lease = await new PostgresJobRepository(claimDb, _clock)
            .TryClaimNextAsync("proof-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(job.Id, lease!.Job.Id);
    }

    [PostgresFact]
    public async Task Duplicate_submit_returns_the_same_job_and_changed_payload_conflicts()
    {
        var body = ValidRequest();
        var first = await SubmitAsync(body, "idem-same");
        var second = await SubmitAsync(body, "idem-same");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(
            (await first.ReadAsync<SubmitNestJobResponse>()).JobId,
            (await second.ReadAsync<SubmitNestJobResponse>()).JobId);

        var conflict = await SubmitAsync(ValidRequest(widthA: 601), "idem-same");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(ApiErrorCodes.IdempotencyConflict, (await conflict.ReadErrorAsync()).Code);

        await using var db = pg.CreateContext();
        Assert.Equal(1, await db.ComputeJobs.CountAsync());
    }

    [PostgresFact]
    public async Task Status_returns_metadata_and_result_is_not_ready_while_queued()
    {
        var submit = await SubmitAsync(ValidRequest(), "status-1");
        var jobId = (await submit.ReadAsync<SubmitNestJobResponse>()).JobId;

        var status = await GetAsAsync(ApiRoutes.ForJobStatus(jobId), _login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.ReadAsync<JobStatusResponse>();
        Assert.Equal(jobId, body.JobId);
        Assert.Equal(JobTypes.Nest, body.JobType);
        Assert.Equal(JobStatus.Queued, body.Status);
        Assert.Equal(0, body.AttemptCount);
        Assert.Null(body.StartedAtUtc);
        Assert.Null(body.CompletedAtUtc);

        var result = await GetAsAsync(ApiRoutes.ForJobResult(jobId), _login.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotReady, (await result.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task A_tenant_cannot_read_another_tenants_job()
    {
        var submit = await SubmitAsync(ValidRequest(), "isolation-1");
        var jobId = (await submit.ReadAsync<SubmitNestJobResponse>()).JobId;
        var otherToken = await CreateOtherTenantTokenAsync();

        var status = await GetAsAsync(ApiRoutes.ForJobStatus(jobId), otherToken);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotFound, (await status.ReadErrorAsync()).Code);

        var result = await GetAsAsync(ApiRoutes.ForJobResult(jobId), otherToken);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotFound, (await result.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Submit_execute_and_fetch_result_end_to_end()
    {
        var body = ValidRequest();
        var inputSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CloudJson.Serialize(body))));
        var jobId = (await (await SubmitAsync(body, "e2e-1")).ReadAsync<SubmitNestJobResponse>()).JobId;

        Assert.True(await RunWorkerOnceAsync("e2e-worker"));

        var status = await (await GetAsAsync(ApiRoutes.ForJobStatus(jobId), _login.AccessToken)).ReadAsync<JobStatusResponse>();
        Assert.Equal(JobStatus.Succeeded, status.Status);
        Assert.Equal(1, status.AttemptCount);
        Assert.NotNull(status.StartedAtUtc);
        Assert.Equal(_clock.Now, status.CompletedAtUtc);
        Assert.NotNull(status.DurationMs);
        Assert.Null(status.ErrorCode);

        var response = await GetAsAsync(ApiRoutes.ForJobResult(jobId), _login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadAsync<NestJobResult>();
        Assert.Equal(jobId, result.JobId);
        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal(EngineVersion.Current, result.EngineVersion);
        Assert.Equal(inputSha, result.InputSha256);
        Assert.Equal(status.DurationMs, result.DurationMs);
        Assert.Equal(1, result.SheetCount);
        Assert.Empty(result.Unplaced);
        Assert.Equal(["A", "B"], result.Placements.Select(p => p.PanelId).Order().ToArray());

        // The hash the API reports is the hash of the bytes actually sitting in the object store, and
        // the placements are exactly what the shared runner produces for this request.
        var storedResult = _factory.ObjectStore.Objects[ObjectKeys.JobResult(Guid.Parse(_login.TenantId), jobId)];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(storedResult.Bytes)), result.ResultSha256);
        var direct = new NestingRunner().Run(NestJobMapper.ToNestingInput(body));
        Assert.Equal(
            direct.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)),
            result.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)));

        await using var db = pg.CreateContext();
        var events = await db.AuditEvents.Where(a => a.JobId == jobId).OrderBy(a => a.Id).Select(a => a.EventType).ToListAsync();
        Assert.Equal(["job.submitted", "job.claimed", "job.started", "job.succeeded"], events);
    }

    [PostgresFact]
    public async Task V2_submit_execute_and_fetch_true_shape_result_end_to_end()
    {
        var request = NestContractV2Mapper.ToRequest(
            [
                new Panel { PanelId = "L", Material = "MDF", ThicknessMm = 18, Outline = new Outline { Points = [new(0, 0), new(700, 0), new(700, 250), new(300, 250), new(300, 500), new(0, 500)] } },
                new Panel { PanelId = "R", Material = "MDF", ThicknessMm = 18, Outline = new Outline { Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)] } },
            ],
            new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true, GrainLock = true },
            [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, Label = "full", Material = "MDF", ThicknessMm = 18 }],
            "nfp",
            TimeSpan.FromSeconds(20));
        var inputSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CloudJson.Serialize(request))));

        using var submitMessage = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.JobsNestV2)
        {
            Content = new StringContent(CloudJson.Serialize(request), Encoding.UTF8, "application/json"),
        };
        submitMessage.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, "v2-e2e-1");
        submitMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _login.AccessToken);
        var submit = await _client.SendAsync(submitMessage);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var jobId = (await submit.ReadAsync<SubmitNestJobResponse>()).JobId;
        Assert.Equal(JobTypes.NestV2, (await (await GetAsAsync(ApiRoutes.ForJobStatus(jobId), _login.AccessToken)).ReadAsync<JobStatusResponse>()).JobType);

        Assert.True(await RunWorkerOnceAsync("v2-worker"));

        var response = await GetAsAsync(ApiRoutes.ForJobResult(jobId), _login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadAsync<NestJobResultV2>();
        Assert.Equal(jobId, result.JobId);
        Assert.Equal(inputSha, result.InputSha256);
        Assert.Equal(EngineVersion.Current, result.EngineVersion);
        Assert.Equal(["L", "R"], result.Result.Placements.Select(p => p.PanelId).Order());
        Assert.Empty(result.Result.Unplaced);
        Assert.Single(result.Result.SheetsUsed);
        Assert.NotEmpty(result.Result.GroupReports);
        Assert.False(string.IsNullOrEmpty(result.Result.Log.SelectedEngine));

        // Same request through the local router gives the same placements: the parity the shop cares about.
        var (local, _) = NestingRunner.RunRouter(
            request.Panels.Select(NestContractV2Mapper.ToPanel).ToList(), NestContractV2Mapper.ToSettings(request.Settings),
            request.Sheets.Select(NestContractV2Mapper.ToSheet).ToList(), "nfp", TimeSpan.FromSeconds(20));
        Assert.Equal(local.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)),
                     result.Result.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)));

        // The v1 shape is not returned for a v2 job (the client deserializes by the type it submitted).
        var invalid = request with { Panels = [] };
        using var bad = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.JobsNestV2) { Content = new StringContent(CloudJson.Serialize(invalid), Encoding.UTF8, "application/json") };
        bad.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, "v2-e2e-bad");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _login.AccessToken);
        var badResponse = await _client.SendAsync(bad);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        // Reusing a v1 key for a v2 body is an idempotency conflict, not a silent type change.
        await SubmitAsync(ValidRequest(), "v2-e2e-mixed");
        using var mixed = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.JobsNestV2) { Content = new StringContent(CloudJson.Serialize(request), Encoding.UTF8, "application/json") };
        mixed.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, "v2-e2e-mixed");
        mixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _login.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(mixed)).StatusCode);
    }

    [PostgresFact]
    public async Task Result_endpoint_refuses_a_stored_result_whose_hash_no_longer_matches()
    {
        var jobId = (await (await SubmitAsync(ValidRequest(), "tamper-1")).ReadAsync<SubmitNestJobResponse>()).JobId;
        Assert.True(await RunWorkerOnceAsync("e2e-worker"));
        var key = ObjectKeys.JobResult(Guid.Parse(_login.TenantId), jobId);
        var original = _factory.ObjectStore.Objects[key];
        var tampered = original.Bytes.ToArray();
        tampered[^2] ^= 0x01;
        await _factory.ObjectStore.PutAsync(key, new MemoryStream(tampered), original.ContentType, CancellationToken.None);

        var response = await GetAsAsync(ApiRoutes.ForJobResult(jobId), _login.AccessToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiErrorCodes.StorageFailed, (await response.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Failed_job_reports_its_error_code_in_status_and_has_no_result()
    {
        var jobId = (await (await SubmitAsync(ValidRequest(), "fail-1")).ReadAsync<SubmitNestJobResponse>()).JobId;
        _factory.ObjectStore.FailPutWhen = key => key.EndsWith("/result.json", StringComparison.Ordinal);
        for (var i = 0; i < PostgresJobRepository.MaxAttempts; i++)
            Assert.True(await RunWorkerOnceAsync("e2e-worker"));

        var status = await (await GetAsAsync(ApiRoutes.ForJobStatus(jobId), _login.AccessToken)).ReadAsync<JobStatusResponse>();
        Assert.Equal(JobStatus.Failed, status.Status);
        Assert.Equal(PostgresJobRepository.MaxAttempts, status.AttemptCount);
        Assert.Equal(ApiErrorCodes.StorageFailed, status.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(status.ErrorMessage));
        Assert.DoesNotContain("   at ", status.ErrorMessage);
        Assert.NotNull(status.CompletedAtUtc);

        var result = await GetAsAsync(ApiRoutes.ForJobResult(jobId), _login.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotReady, (await result.ReadErrorAsync()).Code);
    }

    async Task<bool> RunWorkerOnceAsync(string workerId)
    {
        await using var db = pg.CreateContext();
        var executor = new NestJobExecutor(
            new PostgresJobRepository(db, _clock),
            _factory.ObjectStore,
            new NestingRunner(),
            db,
            _clock,
            new WorkerOptions { WorkerId = workerId },
            NullLogger<NestJobExecutor>.Instance);
        return await executor.ExecuteOneAsync(CancellationToken.None);
    }

    [PostgresFact]
    public async Task Storage_failure_does_not_make_the_job_claimable()
    {
        _factory.ObjectStore.FailPutWhen = key => key.EndsWith("/input.json", StringComparison.Ordinal);

        var response = await SubmitAsync(ValidRequest(), "storage-fail");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiErrorCodes.StorageFailed, (await response.ReadErrorAsync()).Code);
        await using var db = pg.CreateContext();
        var job = await db.ComputeJobs.SingleAsync();
        Assert.Null(job.InputStoredAtUtc);
        await using var claimDb = pg.CreateContext();
        Assert.Null(await new PostgresJobRepository(claimDb, _clock)
            .TryClaimNextAsync("worker", TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    async Task<string> CreateOtherTenantTokenAsync()
    {
        await using var db = pg.CreateContext();
        var tenant = new TenantEntity
        {
            Id = Guid.CreateVersion7(),
            Name = "tenant-b",
            CreatedAtUtc = _clock.Now,
        };
        var user = new UserEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Email = "admin@tenant-b.test",
            PasswordHash = "",
            Role = "admin",
            CreatedAtUtc = _clock.Now,
        };
        user.PasswordHash = new PasswordHasher<UserEntity>().HashPassword(user, "test-only-password");
        var device = new DeviceEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserId = user.Id,
            DeviceKey = Guid.NewGuid().ToString("D"),
            CreatedAtUtc = _clock.Now,
        };
        db.AddRange(tenant, user, device);
        await db.SaveChangesAsync();

        return _factory.Services.GetRequiredService<AccessTokenIssuer>()
            .Issue(new AccessTokenSubject(user.Id, tenant.Id, device.Id, user.Role));
    }
}
