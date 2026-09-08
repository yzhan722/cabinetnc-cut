using System.Security.Cryptography;
using System.Text;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Tests;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Cloud.Worker;
using CabinetNC.Compute.Core.Cam;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CabinetNC.Cloud.Worker.Tests;

/// <summary>
/// The worker loop body against a real PostgreSQL queue, the shared in-memory object store and the
/// real Compute.Core runner: claim â†?load + verify input â†?run â†?store result â†?fenced completion.
/// </summary>
public class NestJobExecutorTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid UserA = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    static readonly Guid DeviceA = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001");
    static readonly CancellationToken CT = CancellationToken.None;

    readonly ManualClock _clock = new();
    readonly TestObjectStore _objects = new();

    public Task InitializeAsync() => PostgresAvailability.IsAvailable ? pg.ResetAsync() : Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    static SubmitNestJobRequest SampleRequest() =>
        new(
            Parts:
            [
                new NestPartDto("A", 600, 400, true, "MDF", 18),
                new NestPartDto("B", 600, 400, true, "MDF", 18),
                new NestPartDto("BIG", 3000, 3000, true, "MDF", 18),
            ],
            SheetWidthMm: 1220,
            SheetLengthMm: 2440,
            SpacingMm: 12,
            BorderMm: 15,
            AllowRotation: true);

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    NestJobExecutor Executor(CloudDbContext db, string workerId = "worker-1", INestingRunner? runner = null, TimeSpan? lease = null) =>
        new(
            new PostgresJobRepository(db, _clock),
            _objects,
            runner ?? new NestingRunner(), new OperationsRunner(), new PostProcessorRunner(),
            db,
            _clock,
            new WorkerOptions { WorkerId = workerId, LeaseDuration = lease ?? TimeSpan.FromMinutes(5) },
            NullLogger<NestJobExecutor>.Instance);

    async Task<ComputeJobEntity> SeedQueuedJobAsync(SubmitNestJobRequest request, byte[]? storedBytes = null, string key = "k1")
    {
        await using var db = pg.CreateContext();
        var repo = new PostgresJobRepository(db, _clock);
        var canonical = Encoding.UTF8.GetBytes(CloudJson.Serialize(request));
        var job = await repo.CreateOrGetByIdempotencyKeyAsync(
            new NewComputeJob(TenantA, UserA, DeviceA, JobTypes.Nest, key, "corr-" + key, Sha256(canonical)), CT);
        await _objects.PutAsync(job.InputObjectKey, new MemoryStream(storedBytes ?? canonical), "application/json", CT);
        Assert.True(await repo.MarkInputStoredAsync(job.Id, CT));
        return job;
    }

    async Task<ComputeJobEntity> LoadAsync(Guid jobId)
    {
        await using var db = pg.CreateContext();
        return await db.ComputeJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    async Task<List<string>> AuditEventsAsync(Guid jobId)
    {
        await using var db = pg.CreateContext();
        return await db.AuditEvents.Where(a => a.JobId == jobId).OrderBy(a => a.Id).Select(a => a.EventType).ToListAsync();
    }

    [PostgresFact]
    public async Task ExecuteOne_returns_false_when_nothing_is_claimable()
    {
        await using var db = pg.CreateContext();

        Assert.False(await Executor(db).ExecuteOneAsync(CT));
    }

    [PostgresFact]
    public async Task ExecuteOne_runs_a_queued_synthetic_nest_and_records_result_hash_version_and_duration()
    {
        var request = SampleRequest();
        var job = await SeedQueuedJobAsync(request);
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var done = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Equal(1, done.AttemptCount);
        Assert.Null(done.LockedBy);
        Assert.Null(done.LockedUntilUtc);
        Assert.Null(done.ErrorCode);
        Assert.Equal(_clock.Now, done.CompletedAtUtc);
        Assert.Equal(ObjectKeys.JobResult(TenantA, job.Id), done.ResultObjectKey);
        Assert.Equal(EngineVersion.Current, done.EngineVersion);
        Assert.False(string.IsNullOrWhiteSpace(done.EngineVersion));
        Assert.NotNull(done.DurationMs);
        Assert.InRange(done.DurationMs!.Value, 0, 60_000);

        var stored = _objects.Objects[done.ResultObjectKey!];
        Assert.Equal("application/json", stored.ContentType);
        Assert.Equal(Sha256(stored.Bytes), done.ResultSha256);

        var payload = CloudJson.Deserialize<NestJobResultPayload>(Encoding.UTF8.GetString(stored.Bytes));
        Assert.Equal("grouped_blf_v0", payload.Engine);
        Assert.Equal(["A", "B"], payload.Placements.Select(p => p.PanelId).Order().ToArray());
        Assert.Equal(["BIG"], payload.Unplaced);
        Assert.Equal(1, payload.SheetCount);

        Assert.Equal(["job.claimed", "job.started", "job.succeeded"], await AuditEventsAsync(job.Id));
    }

    [PostgresFact]
    public async Task Server_result_equals_running_the_shared_runner_directly()
    {
        var request = SampleRequest();
        var job = await SeedQueuedJobAsync(request);
        await using var db = pg.CreateContext();
        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var stored = _objects.Objects[ObjectKeys.JobResult(TenantA, job.Id)];
        var payload = CloudJson.Deserialize<NestJobResultPayload>(Encoding.UTF8.GetString(stored.Bytes));
        var direct = new NestingRunner().Run(NestJobMapper.ToNestingInput(request));

        Assert.True(direct.Ok);
        Assert.Equal(direct.Engine, payload.Engine);
        Assert.Equal(direct.SheetCount, payload.SheetCount);
        Assert.Equal(direct.Unplaced, payload.Unplaced);
        Assert.Equal(
            direct.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)),
            payload.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)));
        Assert.Equal(
            direct.Warnings.Select(w => (w.Code, w.Message, w.PanelIdA, w.PanelIdB, w.SheetIndex)),
            payload.Warnings.Select(w => (w.Code, w.Message, w.PanelIdA, w.PanelIdB, w.SheetIndex)));
    }

    [PostgresFact]
    public async Task Storage_failure_never_marks_succeeded_and_exhausts_the_retry_budget()
    {
        var job = await SeedQueuedJobAsync(SampleRequest());
        _objects.FailPutWhen = key => key.EndsWith("/result.json", StringComparison.Ordinal);
        await using var db = pg.CreateContext();
        var executor = Executor(db);

        for (var attempt = 1; attempt <= PostgresJobRepository.MaxAttempts; attempt++)
        {
            Assert.True(await executor.ExecuteOneAsync(CT));
            var after = await LoadAsync(job.Id);
            Assert.NotEqual(JobStatus.Succeeded, after.Status);
            Assert.Equal(attempt, after.AttemptCount);
            Assert.Equal(ApiErrorCodes.StorageFailed, after.ErrorCode);
            Assert.Null(after.ResultSha256);
            Assert.Equal(attempt < PostgresJobRepository.MaxAttempts ? JobStatus.Queued : JobStatus.Failed, after.Status);
        }

        Assert.False(_objects.Objects.ContainsKey(ObjectKeys.JobResult(TenantA, job.Id)));
        Assert.False(await executor.ExecuteOneAsync(CT), "a Failed job must not be claimable");
        var events = await AuditEventsAsync(job.Id);
        Assert.Equal(2, events.Count(e => e == "job.retry"));
        Assert.Equal(1, events.Count(e => e == "job.failed"));
        Assert.DoesNotContain("job.succeeded", events);
    }

    [PostgresFact]
    public async Task Tampered_input_fails_immediately_without_retry_or_stack_trace()
    {
        var request = SampleRequest();
        var tampered = Encoding.UTF8.GetBytes(CloudJson.Serialize(request with { SpacingMm = 13 }));
        var job = await SeedQueuedJobAsync(request, storedBytes: tampered);
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var failed = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(ApiErrorCodes.StorageFailed, failed.ErrorCode);
        Assert.DoesNotContain("   at ", failed.ErrorMessage);
        Assert.DoesNotContain("Exception:", failed.ErrorMessage);
        Assert.Equal(["job.claimed", "job.started", "job.failed"], await AuditEventsAsync(job.Id));
    }

    [PostgresFact]
    public async Task Unexpected_runner_exception_is_retryable_and_recorded_without_details()
    {
        var job = await SeedQueuedJobAsync(SampleRequest());
        var runner = new ThrowingRunner(new InvalidOperationException(@"boom at C:\secret\path\input.json"));
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db, runner: runner).ExecuteOneAsync(CT));

        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Queued, after.Status);
        Assert.Equal(1, after.AttemptCount);
        Assert.Equal(ApiErrorCodes.ComputeFailed, after.ErrorCode);
        Assert.Contains(nameof(InvalidOperationException), after.ErrorMessage);
        Assert.DoesNotContain("secret", after.ErrorMessage);
        Assert.DoesNotContain("   at ", after.ErrorMessage);
        Assert.Contains("job.retry", await AuditEventsAsync(job.Id));
    }

    [PostgresFact]
    public async Task Runner_reported_failure_is_final_and_keeps_the_runner_message()
    {
        var job = await SeedQueuedJobAsync(SampleRequest());
        var runner = new FixedOutputRunner(new NestingOutput(false, "", [], 0, [], [], "engine rejected the input"));
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db, runner: runner).ExecuteOneAsync(CT));

        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Failed, after.Status);
        Assert.Equal(ApiErrorCodes.ComputeFailed, after.ErrorCode);
        Assert.Equal("engine rejected the input", after.ErrorMessage);
        Assert.False(await Executor(db).ExecuteOneAsync(CT));
    }

    [PostgresFact]
    public async Task Losing_the_lease_mid_run_discards_the_completion()
    {
        var job = await SeedQueuedJobAsync(SampleRequest());
        await using var dbSlow = pg.CreateContext();
        await using var dbOther = pg.CreateContext();
        var otherRepo = new PostgresJobRepository(dbOther, _clock);

        // The slow worker's runner "takes" longer than its lease; meanwhile another worker reclaims the job.
        var slowRunner = new CallbackRunner(input =>
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            var reclaimed = otherRepo.TryClaimNextAsync("other-worker", TimeSpan.FromMinutes(5), CT).GetAwaiter().GetResult();
            Assert.Equal(job.Id, reclaimed!.Job.Id);
            return new NestingRunner().Run(input);
        });

        Assert.True(await Executor(dbSlow, workerId: "slow-worker", runner: slowRunner, lease: TimeSpan.FromMinutes(1)).ExecuteOneAsync(CT));

        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Running, after.Status);
        Assert.Equal("other-worker", after.LockedBy);
        Assert.Equal(2, after.AttemptCount);
        Assert.Null(after.ResultSha256);
        Assert.Null(after.CompletedAtUtc);
        Assert.DoesNotContain("job.succeeded", await AuditEventsAsync(job.Id));
    }

    [PostgresFact]
    public async Task Unsupported_job_type_fails_without_retry()
    {
        await using var seed = pg.CreateContext();
        var repo = new PostgresJobRepository(seed, _clock);
        var job = await repo.CreateOrGetByIdempotencyKeyAsync(
            new NewComputeJob(TenantA, UserA, DeviceA, "carve", "k-carve", "corr", Sha256("{}"u8.ToArray())), CT);
        await _objects.PutAsync(job.InputObjectKey, new MemoryStream("{}"u8.ToArray()), "application/json", CT);
        await repo.MarkInputStoredAsync(job.Id, CT);
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var after = await LoadAsync(job.Id);
        Assert.Equal(JobStatus.Failed, after.Status);
        Assert.Equal(ApiErrorCodes.InvalidRequest, after.ErrorCode);
    }

    [PostgresFact]
    public async Task V2_job_runs_the_true_shape_router_and_stores_the_full_payload()
    {
        var request = NestContractV2Mapper.ToRequest(
            [
                new Panel { PanelId = "L", Material = "MDF", ThicknessMm = 18, Outline = new Outline { Points = [new(0, 0), new(700, 0), new(700, 250), new(300, 250), new(300, 500), new(0, 500)] } },
                new Panel { PanelId = "R", Material = "MDF", ThicknessMm = 18, GrainDirection = "X", AllowedRotations = [0, 180], Outline = new Outline { Points = [new(0, 0), new(600, 0), new(600, 400), new(0, 400)] } },
                new Panel { PanelId = "BIG", Material = "MDF", ThicknessMm = 18, Outline = new Outline { Points = [new(0, 0), new(3000, 0), new(3000, 3000), new(0, 3000)] } },
            ],
            new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true, GrainLock = true },
            [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12, Label = "full", Material = "MDF", ThicknessMm = 18 }],
            "nfp",
            TimeSpan.FromSeconds(20));
        var canonical = Encoding.UTF8.GetBytes(CloudJson.Serialize(request));
        Guid jobId;
        await using (var seed = pg.CreateContext())
        {
            var repo = new PostgresJobRepository(seed, _clock);
            var job = await repo.CreateOrGetByIdempotencyKeyAsync(new NewComputeJob(TenantA, UserA, DeviceA, JobTypes.NestV2, "v2-1", "corr-v2", Sha256(canonical)), CT);
            await _objects.PutAsync(job.InputObjectKey, new MemoryStream(canonical), "application/json", CT);
            Assert.True(await repo.MarkInputStoredAsync(job.Id, CT));
            jobId = job.Id;
        }
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var done = await LoadAsync(jobId);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        var stored = _objects.Objects[ObjectKeys.JobResult(TenantA, jobId)];
        Assert.Equal(Sha256(stored.Bytes), done.ResultSha256);
        var payload = CloudJson.Deserialize<NestJobResultPayloadV2>(Encoding.UTF8.GetString(stored.Bytes));
        var (local, _) = NestingRunner.RunRouter(
            request.Panels.Select(NestContractV2Mapper.ToPanel).ToList(),
            NestContractV2Mapper.ToSettings(request.Settings),
            request.Sheets.Select(NestContractV2Mapper.ToSheet).ToList(),
            "nfp", TimeSpan.FromSeconds(20));
        Assert.Equal(local.Engine, payload.Engine);
        Assert.Equal(["L", "R"], payload.Placements.Select(p => p.PanelId).Order());
        Assert.Equal(["BIG"], payload.Unplaced);
        Assert.Equal(local.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)),
                     payload.Placements.Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg)));
        Assert.Single(payload.SheetsUsed);
        Assert.Equal("full", payload.SheetsUsed[0].Label);
        Assert.NotEmpty(payload.GroupReports);
        Assert.Equal(local.Engine, payload.Log.SelectedEngine);
    }

    [PostgresFact]
    public async Task V2_job_with_an_invalid_request_fails_without_retry()
    {
        await using (var seed = pg.CreateContext())
        {
            var repo = new PostgresJobRepository(seed, _clock);
            var bytes = "{\"panels\":[],\"sheets\":[],\"settings\":null,\"enginePreference\":\"nfp\",\"advancedTimeoutSeconds\":5}"u8.ToArray();
            var job = await repo.CreateOrGetByIdempotencyKeyAsync(new NewComputeJob(TenantA, UserA, DeviceA, JobTypes.NestV2, "v2-bad", "corr", Sha256(bytes)), CT);
            await _objects.PutAsync(job.InputObjectKey, new MemoryStream(bytes), "application/json", CT);
            await repo.MarkInputStoredAsync(job.Id, CT);
        }
        await using var db = pg.CreateContext();

        Assert.True(await Executor(db).ExecuteOneAsync(CT));

        var failed = (await db.ComputeJobs.AsNoTracking().SingleAsync());
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(ApiErrorCodes.InvalidRequest, failed.ErrorCode);
        Assert.Equal(1, failed.AttemptCount);
    }

    sealed class ThrowingRunner(Exception exception) : INestingRunner
    {
        public NestingOutput Run(NestingInput input) => throw exception;
        public NestJobResultPayloadV2 RunV2(SubmitNestJobRequestV2 request, CancellationToken ct = default) => throw exception;
    }

    sealed class FixedOutputRunner(NestingOutput output) : INestingRunner
    {
        public NestingOutput Run(NestingInput input) => output;
        public NestJobResultPayloadV2 RunV2(SubmitNestJobRequestV2 request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class CallbackRunner(Func<NestingInput, NestingOutput> callback) : INestingRunner
    {
        public NestingOutput Run(NestingInput input) => callback(input);
        public NestJobResultPayloadV2 RunV2(SubmitNestJobRequestV2 request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
