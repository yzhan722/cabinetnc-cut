using System.Data;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CabinetNC.Cloud.Infrastructure.Jobs;

/// <summary>
/// Every state transition is a single conditional SQL statement so two workers, or a worker and its
/// own zombie, can never both win. The claim relies on PostgreSQL row locks (<c>FOR UPDATE SKIP LOCKED</c>)
/// and honours an ambient EF transaction when the caller opened one.
/// </summary>
public sealed class PostgresJobRepository(CloudDbContext db, TimeProvider clock) : IJobRepository
{
    public const int MaxAttempts = 3;

    public async Task<ComputeJobEntity> CreateOrGetByIdempotencyKeyAsync(NewComputeJob job, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var entity = new ComputeJobEntity
        {
            Id = id,
            TenantId = job.TenantId,
            UserId = job.UserId,
            DeviceId = job.DeviceId,
            JobType = job.JobType,
            Status = JobStatus.Queued,
            IdempotencyKey = job.IdempotencyKey,
            CorrelationId = job.CorrelationId,
            InputObjectKey = ObjectKeys.JobInput(job.TenantId, id),
            InputSha256 = job.InputSha256,
            AttemptCount = 0,
            CreatedAtUtc = clock.GetUtcNow(),
        };

        db.ComputeJobs.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            db.Entry(entity).State = EntityState.Detached;
            return entity;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the race (or a genuine retry): the unique index guarantees exactly one row exists.
            db.Entry(entity).State = EntityState.Detached;
            return await db.ComputeJobs.AsNoTracking().SingleAsync(
                j => j.TenantId == job.TenantId && j.UserId == job.UserId && j.IdempotencyKey == job.IdempotencyKey, ct);
        }
    }

    public async Task<bool> MarkInputStoredAsync(Guid jobId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ComputeJobs"
            SET "InputStoredAtUtc" = COALESCE("InputStoredAtUtc", {now})
            WHERE "Id" = {jobId}
            """, ct);
        return rows == 1;
    }

    public async Task<JobLease?> TryClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var until = now + leaseDuration;

        // A worker that died on the last permitted attempt leaves an expired Running row nobody may retry.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ComputeJobs"
            SET "Status" = {nameof(JobStatus.Failed)},
                "LockedBy" = NULL,
                "LockedUntilUtc" = NULL,
                "CompletedAtUtc" = {now},
                "ErrorCode" = {ApiErrorCodes.ComputeFailed},
                "ErrorMessage" = {"lease expired after " + MaxAttempts + " attempts without a result"}
            WHERE "Status" = {nameof(JobStatus.Running)}
              AND "LockedUntilUtc" < {now}
              AND "AttemptCount" >= {MaxAttempts}
            """, ct);

        var claimedId = await ClaimOneAsync(workerId, now, until, ct);
        if (claimedId is null)
            return null;

        var job = await db.ComputeJobs.AsNoTracking().SingleAsync(j => j.Id == claimedId.Value, ct);
        return new JobLease(job, workerId, until);
    }

    async Task<Guid?> ClaimOneAsync(string workerId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct)
    {
        const string sql = """
            UPDATE "ComputeJobs" AS j
            SET "Status" = @running,
                "AttemptCount" = j."AttemptCount" + 1,
                "LockedBy" = @worker,
                "LockedUntilUtc" = @until,
                "StartedAtUtc" = COALESCE(j."StartedAtUtc", @now)
            FROM (
                SELECT "Id"
                FROM "ComputeJobs"
                WHERE (("Status" = @queued AND "InputStoredAtUtc" IS NOT NULL)
                    OR ("Status" = @running AND "LockedUntilUtc" < @now))
                  AND "AttemptCount" < @max
                ORDER BY "CreatedAtUtc", "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            ) AS c
            WHERE j."Id" = c."Id"
            RETURNING j."Id"
            """;

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.Add(new NpgsqlParameter("running", nameof(JobStatus.Running)));
            command.Parameters.Add(new NpgsqlParameter("queued", nameof(JobStatus.Queued)));
            command.Parameters.Add(new NpgsqlParameter("worker", workerId));
            command.Parameters.Add(new NpgsqlParameter("until", until));
            command.Parameters.Add(new NpgsqlParameter("now", now));
            command.Parameters.Add(new NpgsqlParameter("max", MaxAttempts));
            var result = await command.ExecuteScalarAsync(ct);
            return result is Guid id ? id : null;
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> MarkSucceededAsync(Guid jobId, string workerId, JobCompletion completion, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ComputeJobs"
            SET "Status" = {nameof(JobStatus.Succeeded)},
                "ResultObjectKey" = {completion.ResultObjectKey},
                "ResultSha256" = {completion.ResultSha256},
                "EngineVersion" = {completion.EngineVersion},
                "DurationMs" = {completion.DurationMs},
                "CompletedAtUtc" = {now},
                "LockedBy" = NULL,
                "LockedUntilUtc" = NULL,
                "ErrorCode" = NULL,
                "ErrorMessage" = NULL
            WHERE "Id" = {jobId}
              AND "Status" = {nameof(JobStatus.Running)}
              AND "LockedBy" = {workerId}
            """, ct);
        return rows == 1;
    }

    public async Task<JobStatus?> MarkFailedAsync(Guid jobId, string workerId, string errorCode, string errorMessage, bool retryable, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ComputeJobs"
            SET "Status" = {nameof(JobStatus.Failed)},
                "ErrorCode" = {errorCode},
                "ErrorMessage" = {errorMessage},
                "CompletedAtUtc" = {now},
                "LockedBy" = NULL,
                "LockedUntilUtc" = NULL
            WHERE "Id" = {jobId}
              AND "Status" = {nameof(JobStatus.Running)}
              AND "LockedBy" = {workerId}
              AND ({!retryable} OR "AttemptCount" >= {MaxAttempts})
            """, ct);
        if (failed == 1)
            return JobStatus.Failed;

        var requeued = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ComputeJobs"
            SET "Status" = {nameof(JobStatus.Queued)},
                "ErrorCode" = {errorCode},
                "ErrorMessage" = {errorMessage},
                "LockedBy" = NULL,
                "LockedUntilUtc" = NULL
            WHERE "Id" = {jobId}
              AND "Status" = {nameof(JobStatus.Running)}
              AND "LockedBy" = {workerId}
            """, ct);
        return requeued == 1 ? JobStatus.Queued : null;
    }

    public Task<ComputeJobEntity?> GetAsync(Guid jobId, Guid tenantId, CancellationToken ct) =>
        db.ComputeJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId && j.TenantId == tenantId, ct);
}
