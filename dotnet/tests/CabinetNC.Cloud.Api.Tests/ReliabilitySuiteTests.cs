using System.Net;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Tests;
using CabinetNC.Cloud.Worker;
using CabinetNC.Compute.Core.Cam;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.Desktop.Core.Cloud;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>
/// Task 10 failure suite (design spec §16 gate D). Every scenario runs the real Desktop client
/// (AuthSession + bearer handler + polling gateway) against the real API, PostgreSQL and worker loop
/// body; only the object store is an in-memory double so outages can be injected.
/// </summary>
public class ReliabilitySuiteTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    static readonly CancellationToken CT = CancellationToken.None;
    static readonly SubmitNestJobRequest Request = new(
        [new NestPartDto("A", 600, 400, true, "MDF", 18), new NestPartDto("B", 350, 250, false, "MDF", 18), new NestPartDto("BIG", 3000, 3000, true, "MDF", 18)],
        1220, 2440, 12, 15, true);

    readonly ManualClock _clock = new() { Now = Truncate(DateTimeOffset.UtcNow) };
    readonly string _deviceDir = Path.Combine(Path.GetTempPath(), "cabinetnc-reliability-" + Guid.NewGuid().ToString("N"));
    readonly MemoryTokenStore _tokens = new();
    CloudApiFactory _factory = null!;

    static DateTimeOffset Truncate(DateTimeOffset v) => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, v.Second, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;
        await pg.ResetAsync();
        Directory.CreateDirectory(_deviceDir);
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        try { Directory.Delete(_deviceDir, true); } catch { /* best effort */ }
    }

    CloudApiClient NewClient(CloudApiFactory? factory = null, MemoryTokenStore? tokens = null, string? deviceDir = null, TimeSpan? jobTimeout = null)
    {
        factory ??= _factory;
        var options = new CloudClientOptions
        {
            BaseAddress = factory.Server.BaseAddress,
            Tenant = CloudApiFactory.Tenant,
            NetworkGrace = TimeSpan.FromSeconds(30),
            JobTimeout = jobTimeout ?? TimeSpan.FromMinutes(10),
        };
        return new CloudApiClient(options, tokens ?? _tokens, new DeviceIdentityStore(deviceDir ?? _deviceDir), _clock, factory.Server.CreateHandler());
    }

    async Task<CloudApiClient> LoggedInClientAsync(CloudApiFactory? factory = null, TimeSpan? jobTimeout = null)
    {
        var client = NewClient(factory, jobTimeout: jobTimeout);
        await client.Session.LoginAsync(CloudApiFactory.AdminEmail, CloudApiFactory.AdminPassword, CT);
        return client;
    }

    /// <summary>A gateway whose poll delays run <paramref name="onPoll"/> (the "world" advancing) instead of sleeping.</summary>
    IntranetComputeGateway Gateway(CloudApiClient client, Func<int, Task>? onPoll = null)
    {
        var polls = 0;
        return new IntranetComputeGateway(client, client.Options, _clock, async (delay, _) =>
        {
            polls++;
            _clock.Advance(delay);
            if (onPoll is not null)
                await onPoll(polls);
        });
    }

    async Task<bool> RunWorkerOnceAsync(string workerId = "worker-1", CloudApiFactory? factory = null)
    {
        await using var db = pg.CreateContext();
        var executor = new NestJobExecutor(new PostgresJobRepository(db, _clock), (factory ?? _factory).ObjectStore, new NestingRunner(), new OperationsRunner(), new PostProcessorRunner(), db, _clock,
            new WorkerOptions { WorkerId = workerId }, NullLogger<NestJobExecutor>.Instance);
        return await executor.ExecuteOneAsync(CT);
    }

    async Task<ComputeJobEntity> LoadJobAsync(Guid jobId)
    {
        await using var db = pg.CreateContext();
        return await db.ComputeJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    [PostgresFact]
    public async Task Duplicate_submission_yields_exactly_one_job()
    {
        using var client = await LoggedInClientAsync();

        var first = await client.SubmitNestJobAsync(Request, "dup-key", CT);
        var second = await client.SubmitNestJobAsync(Request, "dup-key", CT);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SubmitNestJobAsync(Request, "dup-key", CT)));

        Assert.Equal(first.JobId, second.JobId);
        Assert.All(parallel, p => Assert.Equal(first.JobId, p.JobId));
        await using var db = pg.CreateContext();
        Assert.Equal(1, await db.ComputeJobs.CountAsync());
    }

    [PostgresFact]
    public async Task Worker_crash_after_claim_is_recovered_through_lease_expiry()
    {
        using var client = await LoggedInClientAsync();
        Guid? jobId = null;
        var gateway = Gateway(client, async poll =>
        {
            switch (poll)
            {
                case 1:
                    // A worker claims the job and dies without ever reporting back.
                    await using (var db = pg.CreateContext())
                    {
                        var lease = await new PostgresJobRepository(db, _clock).TryClaimNextAsync("crashed-worker", TimeSpan.FromMinutes(5), CT);
                        jobId = lease!.Job.Id;
                    }
                    break;
                case 2:
                    _clock.Advance(TimeSpan.FromMinutes(6));   // lease expires while the Desktop keeps polling
                    break;
                case 3:
                    Assert.True(await RunWorkerOnceAsync("healthy-worker"));
                    break;
            }
        });

        var result = await gateway.RunNestingAsync(Request, null, CT);

        var job = await LoadJobAsync(result.JobId);
        Assert.Equal(jobId, result.JobId);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.AttemptCount);
        Assert.Null(job.LockedBy);
        Assert.Equal(["A", "B"], result.Placements.Select(p => p.PanelId).Order());
        // The dead worker never got to audit anything; the recovery attempt is fully audited.
        await using var audit = pg.CreateContext();
        var events = await audit.AuditEvents.Where(a => a.JobId == result.JobId).OrderBy(a => a.Id).Select(a => a.EventType).ToListAsync();
        Assert.Equal(["job.submitted", "job.claimed", "job.started", "job.succeeded"], events);
    }

    [PostgresFact]
    public async Task Api_restart_keeps_jobs_and_sessions()
    {
        Guid jobId;
        using (var before = await LoggedInClientAsync())
            jobId = (await before.SubmitNestJobAsync(Request, "restart-1", CT)).JobId;

        // "Restart": tear the API process down and start a fresh one on the same database.
        var oldStore = _factory.ObjectStore;
        await _factory.DisposeAsync();
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
        foreach (var (key, obj) in oldStore.Objects)
            await _factory.ObjectStore.PutAsync(key, new MemoryStream(obj.Bytes), obj.ContentType, CT);

        using var after = NewClient();
        Assert.True(await after.Session.TryRestoreAsync(CT), "the refresh token must survive an API restart");
        var status = await after.GetJobStatusAsync(jobId, CT);
        Assert.Equal(JobStatus.Queued, status.Status);

        Assert.True(await RunWorkerOnceAsync());
        var result = await after.GetJobResultAsync(jobId, CT);
        Assert.Equal(jobId, result.JobId);
        Assert.Equal(1, (await LoadJobAsync(jobId)).AttemptCount);
    }

    [PostgresFact]
    public async Task Object_store_outage_never_produces_a_success()
    {
        using var client = await LoggedInClientAsync();

        // Outage while storing the input: the submit fails, nothing is claimable, and the same key heals later.
        _factory.ObjectStore.FailPutWhen = key => key.EndsWith("/input.json", StringComparison.Ordinal);
        var submitError = await Assert.ThrowsAsync<CloudApiException>(() => client.SubmitNestJobAsync(Request, "outage-1", CT));
        Assert.Equal(HttpStatusCode.InternalServerError, submitError.StatusCode);
        Assert.Equal(ApiErrorCodes.StorageFailed, submitError.Code);
        Assert.False(await RunWorkerOnceAsync(), "a job without stored input must not be claimable");
        _factory.ObjectStore.FailPutWhen = null;
        var healed = await client.SubmitNestJobAsync(Request, "outage-1", CT);
        Assert.NotNull((await LoadJobAsync(healed.JobId)).InputStoredAtUtc);

        // Outage while storing the result: the worker retries within its budget and the job ends Failed, never Succeeded.
        _factory.ObjectStore.FailPutWhen = key => key.EndsWith("/result.json", StringComparison.Ordinal);
        var gateway = Gateway(client, async _ => { await RunWorkerOnceAsync(); });
        var failure = await Assert.ThrowsAsync<ComputeJobFailedException>(() => gateway.RunNestingAsync(Request, null, CT));
        Assert.Equal(ApiErrorCodes.StorageFailed, failure.ErrorCode);
        var job = await LoadJobAsync(failure.JobId);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(PostgresJobRepository.MaxAttempts, job.AttemptCount);
        Assert.Null(job.ResultSha256);
        await using var db = pg.CreateContext();
        Assert.Equal(0, await db.ComputeJobs.CountAsync(j => j.Status == JobStatus.Succeeded));
    }

    [PostgresFact]
    public async Task Access_token_expiry_during_polling_is_refreshed_transparently()
    {
        // A long-running job: the client keeps polling past the 15-minute access token lifetime.
        using var client = await LoggedInClientAsync(jobTimeout: TimeSpan.FromHours(1));
        var firstExpiry = client.Session.AccessTokenExpiresAtUtc;
        var gateway = Gateway(client, async poll =>
        {
            if (poll == 1)
                _clock.Advance(TimeSpan.FromMinutes(16));   // the 15-minute access token is now dead server-side
            if (poll == 2)
                Assert.True(await RunWorkerOnceAsync());
        });

        var result = await gateway.RunNestingAsync(Request, null, CT);

        Assert.Equal(JobStatus.Succeeded, (await LoadJobAsync(result.JobId)).Status);
        Assert.True(client.Session.IsAuthenticated);
        Assert.True(client.Session.AccessTokenExpiresAtUtc > firstExpiry, "a new access token must have been issued");
        await using var db = pg.CreateContext();
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.RevokedAtUtc != null));
    }

    [PostgresFact]
    public async Task Cross_tenant_reads_are_blocked_for_the_client_too()
    {
        using var clientA = await LoggedInClientAsync();
        var jobId = (await clientA.SubmitNestJobAsync(Request, "tenant-a-1", CT)).JobId;

        await using (var db = pg.CreateContext())
        {
            var tenant = new TenantEntity { Id = Guid.CreateVersion7(), Name = "tenant-b", CreatedAtUtc = _clock.Now };
            var user = new UserEntity { Id = Guid.CreateVersion7(), TenantId = tenant.Id, Email = "op@tenant-b.test", PasswordHash = "", Role = "operator", CreatedAtUtc = _clock.Now };
            user.PasswordHash = new PasswordHasher<UserEntity>().HashPassword(user, "tenant-b-password");
            db.AddRange(tenant, user);
            await db.SaveChangesAsync();
        }
        var optionsB = new CloudClientOptions { BaseAddress = _factory.Server.BaseAddress, Tenant = "tenant-b" };
        var dirB = Path.Combine(_deviceDir, "b");
        using var clientB = new CloudApiClient(optionsB, new MemoryTokenStore(), new DeviceIdentityStore(dirB), _clock, _factory.Server.CreateHandler());
        await clientB.Session.LoginAsync("op@tenant-b.test", "tenant-b-password", CT);

        var status = await Assert.ThrowsAsync<CloudApiException>(() => clientB.GetJobStatusAsync(jobId, CT));
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotFound, status.Code);
        var result = await Assert.ThrowsAsync<CloudApiException>(() => clientB.GetJobResultAsync(jobId, CT));
        Assert.Equal(ApiErrorCodes.JobNotFound, result.Code);
    }

    [PostgresFact]
    public async Task Client_disconnect_after_submit_reconnects_to_the_same_job()
    {
        Guid jobId;
        using (var first = await LoggedInClientAsync())
            jobId = (await first.SubmitNestJobAsync(Request, "reconnect-1", CT)).JobId;
        // The Desktop process is gone; the job keeps going on the server.
        Assert.True(await RunWorkerOnceAsync());

        using var second = NewClient();
        Assert.True(await second.Session.TryRestoreAsync(CT));
        var status = await second.GetJobStatusAsync(jobId, CT);
        Assert.Equal(JobStatus.Succeeded, status.Status);
        var result = await second.GetJobResultAsync(jobId, CT);
        Assert.Equal(jobId, result.JobId);

        // Even if the JobId itself was lost, the idempotency key finds the same job instead of recomputing.
        var resubmitted = await second.SubmitNestJobAsync(Request, "reconnect-1", CT);
        Assert.Equal(jobId, resubmitted.JobId);
        Assert.Equal(JobStatus.Succeeded, resubmitted.Status);
        await using var db = pg.CreateContext();
        Assert.Equal(1, await db.ComputeJobs.CountAsync());
    }

    sealed class MemoryTokenStore : ITokenStore
    {
        StoredRefreshToken? _stored;
        public StoredRefreshToken? Load() => _stored;
        public void Save(StoredRefreshToken token) => _stored = token;
        public void Clear() => _stored = null;
    }
}
