using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>
/// Real-PostgreSQL proof of the job queue semantics: idempotent create, FOR UPDATE SKIP LOCKED claim,
/// lease expiry, fenced completion, retry budget. Every test starts from truncated tables.
/// </summary>
public class JobRepositoryTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid UserA = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    static readonly Guid DeviceA = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000001");
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    static readonly CancellationToken CT = CancellationToken.None;
    static readonly JobCompletion Completion = new("tenant/x/jobs/y/result.json", new string('b', 64), "1.0.0+test", 42);

    readonly ManualClock _clock = new();

    public Task InitializeAsync() => PostgresAvailability.IsAvailable ? pg.ResetAsync() : Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    IJobRepository Repo(CloudDbContext db) => new PostgresJobRepository(db, _clock);

    static NewComputeJob Request(Guid tenant, Guid user, string key, string sha = "a") =>
        new(tenant, user, DeviceA, JobTypes.Nest, key, "corr-" + key, new string(sha[0], 64));

    async Task<ComputeJobEntity> NewStoredJob(string key = "k1", Guid? tenant = null)
    {
        await using var db = pg.CreateContext();
        var repo = Repo(db);
        var job = await repo.CreateOrGetByIdempotencyKeyAsync(Request(tenant ?? TenantA, UserA, key), CT);
        Assert.True(await repo.MarkInputStoredAsync(job.Id, CT));
        return job;
    }

    [PostgresFact]
    public async Task Duplicate_idempotency_key_returns_same_job()
    {
        await using var db1 = pg.CreateContext();
        await using var db2 = pg.CreateContext();

        var first = await Repo(db1).CreateOrGetByIdempotencyKeyAsync(Request(TenantA, UserA, "idem-1", sha: "a"), CT);
        // Second request from another connection with a different payload hash: the stored job wins,
        // and the caller sees the original hash so it can raise idempotency_conflict.
        var second = await Repo(db2).CreateOrGetByIdempotencyKeyAsync(Request(TenantA, UserA, "idem-1", sha: "c"), CT);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.InputSha256, second.InputSha256);
        Assert.Equal(new string('a', 64), second.InputSha256);
        Assert.Equal(JobStatus.Queued, first.Status);
        Assert.Equal(0, first.AttemptCount);
        Assert.Null(first.InputStoredAtUtc);
        Assert.Equal(_clock.Now, first.CreatedAtUtc);
        Assert.Equal($"tenant/{TenantA:D}/jobs/{first.Id:D}/input.json", first.InputObjectKey);
        Assert.Equal(1, await db1.ComputeJobs.CountAsync(CT));
    }

    [PostgresFact]
    public async Task Same_idempotency_key_across_tenants_creates_separate_jobs()
    {
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        var a = await repo.CreateOrGetByIdempotencyKeyAsync(Request(TenantA, UserA, "shared-key"), CT);
        var b = await repo.CreateOrGetByIdempotencyKeyAsync(Request(TenantB, UserA, "shared-key"), CT);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(TenantA, a.TenantId);
        Assert.Equal(TenantB, b.TenantId);
        Assert.Equal(2, await db.ComputeJobs.CountAsync(CT));
    }

    [PostgresFact]
    public async Task Two_simultaneous_workers_only_one_claims_the_job()
    {
        var job = await NewStoredJob();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = pg.CreateContext();
            return await Repo(db).TryClaimNextAsync($"worker-{i}", Lease, CT);
        }));

        var lease = Assert.Single(results.Where(r => r is not null))!;
        Assert.Equal(job.Id, lease.Job.Id);
        Assert.Equal(JobStatus.Running, lease.Job.Status);
        Assert.Equal(1, lease.Job.AttemptCount);
        Assert.Equal(lease.WorkerId, lease.Job.LockedBy);
        Assert.Equal(_clock.Now + Lease, lease.LockedUntilUtc);
        Assert.Equal(lease.LockedUntilUtc, lease.Job.LockedUntilUtc);
        Assert.Equal(_clock.Now, lease.Job.StartedAtUtc);
    }

    [PostgresFact]
    public async Task Claim_skips_rows_locked_by_an_uncommitted_transaction()
    {
        var job1 = await NewStoredJob("k1");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var job2 = await NewStoredJob("k2");

        await using var dbA = pg.CreateContext();
        await using var tx = await dbA.Database.BeginTransactionAsync(CT);
        var leaseA = await Repo(dbA).TryClaimNextAsync("A", Lease, CT);
        Assert.Equal(job1.Id, leaseA!.Job.Id);

        // Worker B on its own connection must neither block on job1 nor claim it.
        await using var dbB = pg.CreateContext();
        var repoB = Repo(dbB);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var leaseB = await repoB.TryClaimNextAsync("B", Lease, guard.Token);
        Assert.Equal(job2.Id, leaseB!.Job.Id);
        Assert.Null(await repoB.TryClaimNextAsync("B", Lease, guard.Token));

        await tx.CommitAsync(CT);
        Assert.Null(await repoB.TryClaimNextAsync("B", Lease, CT));
        var committed = await repoB.GetAsync(job1.Id, TenantA, CT);
        Assert.Equal("A", committed!.LockedBy);
        Assert.Equal(JobStatus.Running, committed.Status);
    }

    [PostgresFact]
    public async Task Expired_lease_is_reclaimable_and_the_old_worker_is_fenced_out()
    {
        var job = await NewStoredJob();
        await using var dbA = pg.CreateContext();
        await using var dbB = pg.CreateContext();
        var repoA = Repo(dbA);
        var repoB = Repo(dbB);

        var leaseA = await repoA.TryClaimNextAsync("A", TimeSpan.FromMinutes(1), CT);
        Assert.Equal(job.Id, leaseA!.Job.Id);
        Assert.Null(await repoB.TryClaimNextAsync("B", TimeSpan.FromMinutes(1), CT));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var leaseB = await repoB.TryClaimNextAsync("B", TimeSpan.FromMinutes(1), CT);
        Assert.Equal(job.Id, leaseB!.Job.Id);
        Assert.Equal(2, leaseB.Job.AttemptCount);
        Assert.Equal("B", leaseB.Job.LockedBy);
        Assert.Equal(_clock.Now + TimeSpan.FromMinutes(1), leaseB.LockedUntilUtc);
        Assert.Equal(leaseA.Job.StartedAtUtc, leaseB.Job.StartedAtUtc);

        // A wakes up late: it no longer holds the lease, so it cannot complete or fail the job.
        Assert.False(await repoA.MarkSucceededAsync(job.Id, "A", Completion, CT));
        Assert.Null(await repoA.MarkFailedAsync(job.Id, "A", ApiErrorCodes.ComputeFailed, "late", retryable: true, CT));
        Assert.True(await repoB.MarkSucceededAsync(job.Id, "B", Completion, CT));

        var final = await repoB.GetAsync(job.Id, TenantA, CT);
        Assert.Equal(JobStatus.Succeeded, final!.Status);
        Assert.Equal(Completion.ResultObjectKey, final.ResultObjectKey);
        Assert.Equal(Completion.ResultSha256, final.ResultSha256);
        Assert.Equal(Completion.EngineVersion, final.EngineVersion);
        Assert.Equal(Completion.DurationMs, final.DurationMs);
        Assert.Equal(_clock.Now, final.CompletedAtUtc);
        Assert.Null(final.LockedBy);
        Assert.Null(final.LockedUntilUtc);
        Assert.Null(final.ErrorCode);
        Assert.False(await repoB.MarkSucceededAsync(job.Id, "B", Completion, CT), "completion must not be repeatable");
    }

    [PostgresFact]
    public async Task Third_retryable_failure_marks_job_failed()
    {
        var job = await NewStoredJob();
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var lease = await repo.TryClaimNextAsync("W", Lease, CT);
            Assert.NotNull(lease);
            Assert.Equal(attempt, lease!.Job.AttemptCount);

            var outcome = await repo.MarkFailedAsync(job.Id, "W", ApiErrorCodes.ComputeFailed, $"boom {attempt}", retryable: true, CT);
            Assert.Equal(attempt < PostgresJobRepository.MaxAttempts ? JobStatus.Queued : JobStatus.Failed, outcome);

            var after = await repo.GetAsync(job.Id, TenantA, CT);
            Assert.Null(after!.LockedBy);
            Assert.Null(after.LockedUntilUtc);
            Assert.Equal($"boom {attempt}", after.ErrorMessage);
        }

        var final = await repo.GetAsync(job.Id, TenantA, CT);
        Assert.Equal(JobStatus.Failed, final!.Status);
        Assert.Equal(3, final.AttemptCount);
        Assert.Equal(ApiErrorCodes.ComputeFailed, final.ErrorCode);
        Assert.Equal(_clock.Now, final.CompletedAtUtc);
        Assert.Null(await repo.TryClaimNextAsync("W", Lease, CT));
    }

    [PostgresFact]
    public async Task Non_retryable_failure_fails_on_first_attempt()
    {
        var job = await NewStoredJob();
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        var lease = await repo.TryClaimNextAsync("W", Lease, CT);
        Assert.NotNull(lease);
        var outcome = await repo.MarkFailedAsync(job.Id, "W", ApiErrorCodes.InvalidRequest, "zero parts", retryable: false, CT);

        Assert.Equal(JobStatus.Failed, outcome);
        var final = await repo.GetAsync(job.Id, TenantA, CT);
        Assert.Equal(1, final!.AttemptCount);
        Assert.Equal(ApiErrorCodes.InvalidRequest, final.ErrorCode);
        Assert.Equal("zero parts", final.ErrorMessage);
        Assert.NotNull(final.CompletedAtUtc);
        Assert.Null(await repo.TryClaimNextAsync("W", Lease, CT));
    }

    [PostgresFact]
    public async Task Job_without_stored_input_is_not_claimable()
    {
        await using var db = pg.CreateContext();
        var repo = Repo(db);
        var job = await repo.CreateOrGetByIdempotencyKeyAsync(Request(TenantA, UserA, "pending"), CT);

        Assert.Null(await repo.TryClaimNextAsync("W", Lease, CT));

        Assert.True(await repo.MarkInputStoredAsync(job.Id, CT));
        Assert.True(await repo.MarkInputStoredAsync(job.Id, CT), "marking twice is harmless");
        Assert.False(await repo.MarkInputStoredAsync(Guid.NewGuid(), CT));
        var stored = await repo.GetAsync(job.Id, TenantA, CT);
        Assert.Equal(_clock.Now, stored!.InputStoredAtUtc);

        var lease = await repo.TryClaimNextAsync("W", Lease, CT);
        Assert.Equal(job.Id, lease!.Job.Id);
    }

    [PostgresFact]
    public async Task Worker_that_keeps_dying_is_failed_after_max_attempts()
    {
        var job = await NewStoredJob();
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        for (var i = 1; i <= PostgresJobRepository.MaxAttempts; i++)
        {
            var lease = await repo.TryClaimNextAsync("W", TimeSpan.FromMinutes(1), CT);
            Assert.Equal(i, lease!.Job.AttemptCount);
            _clock.Advance(TimeSpan.FromMinutes(2));   // worker crashes without reporting
        }

        Assert.Null(await repo.TryClaimNextAsync("W", TimeSpan.FromMinutes(1), CT));
        var final = await repo.GetAsync(job.Id, TenantA, CT);
        Assert.Equal(JobStatus.Failed, final!.Status);
        Assert.Equal(PostgresJobRepository.MaxAttempts, final.AttemptCount);
        Assert.Equal(ApiErrorCodes.ComputeFailed, final.ErrorCode);
        Assert.Contains("lease expired", final.ErrorMessage);
        Assert.Equal(_clock.Now, final.CompletedAtUtc);
        Assert.Null(final.LockedBy);
    }

    [PostgresFact]
    public async Task Claims_are_served_oldest_first()
    {
        var first = await NewStoredJob("k1");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await NewStoredJob("k2");
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        Assert.Equal(first.Id, (await repo.TryClaimNextAsync("W", Lease, CT))!.Job.Id);
        Assert.Equal(second.Id, (await repo.TryClaimNextAsync("W", Lease, CT))!.Job.Id);
        Assert.Null(await repo.TryClaimNextAsync("W", Lease, CT));
    }

    [PostgresFact]
    public async Task GetAsync_enforces_tenant_isolation()
    {
        var job = await NewStoredJob();
        await using var db = pg.CreateContext();
        var repo = Repo(db);

        Assert.NotNull(await repo.GetAsync(job.Id, TenantA, CT));
        Assert.Null(await repo.GetAsync(job.Id, TenantB, CT));
        Assert.Null(await repo.GetAsync(Guid.NewGuid(), TenantA, CT));
    }

    [PostgresFact]
    public async Task Schema_enforces_the_planned_unique_constraints()
    {
        await using var db = pg.CreateContext();
        var now = _clock.Now;
        db.Tenants.AddRange(
            new TenantEntity { Id = TenantA, Name = "A", CreatedAtUtc = now },
            new TenantEntity { Id = TenantB, Name = "B", CreatedAtUtc = now });
        db.Users.Add(new UserEntity { Id = UserA, TenantId = TenantA, Email = "op@a.test", PasswordHash = "h", Role = "operator", CreatedAtUtc = now });
        db.Devices.Add(new DeviceEntity { Id = DeviceA, TenantId = TenantA, UserId = UserA, DeviceKey = "dev-1", CreatedAtUtc = now });
        db.RefreshTokens.Add(new RefreshTokenEntity { Id = Guid.NewGuid(), TenantId = TenantA, UserId = UserA, DeviceId = DeviceA, FamilyId = Guid.NewGuid(), TokenHash = new string('f', 64), CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30) });
        await db.SaveChangesAsync(CT);

        await AssertUniqueViolation(new UserEntity { Id = Guid.NewGuid(), TenantId = TenantA, Email = "op@a.test", PasswordHash = "h", Role = "operator", CreatedAtUtc = now });
        await AssertUniqueViolation(new DeviceEntity { Id = Guid.NewGuid(), TenantId = TenantA, UserId = UserA, DeviceKey = "dev-1", CreatedAtUtc = now });
        await AssertUniqueViolation(new RefreshTokenEntity { Id = Guid.NewGuid(), TenantId = TenantA, UserId = UserA, DeviceId = DeviceA, FamilyId = Guid.NewGuid(), TokenHash = new string('f', 64), CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30) });

        // Same email / device key under another tenant is allowed.
        await using var other = pg.CreateContext();
        other.Users.Add(new UserEntity { Id = Guid.NewGuid(), TenantId = TenantB, Email = "op@a.test", PasswordHash = "h", Role = "operator", CreatedAtUtc = now });
        other.Devices.Add(new DeviceEntity { Id = Guid.NewGuid(), TenantId = TenantB, UserId = UserA, DeviceKey = "dev-1", CreatedAtUtc = now });
        await other.SaveChangesAsync(CT);
        Assert.Equal(2, await other.Users.CountAsync(CT));

        async Task AssertUniqueViolation<T>(T entity) where T : class
        {
            await using var ctx = pg.CreateContext();
            ctx.Add(entity);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync(CT));
            var violation = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        }
    }
}
