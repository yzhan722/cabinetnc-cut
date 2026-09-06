using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CabinetNC.Cloud.Api.Auth;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Entities;
using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Tests;
using CabinetNC.Cloud.Worker;
using CabinetNC.Compute.Core.Nesting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>Support must be able to explain any job from its id alone — without ever seeing credentials.</summary>
public class AdminDiagnosticsTests(MigratedByAppPostgresFixture pg) : IClassFixture<MigratedByAppPostgresFixture>, IAsyncLifetime
{
    readonly ManualClock _clock = new() { Now = new DateTimeOffset(DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, DateTimeOffset.UtcNow.Day, DateTimeOffset.UtcNow.Hour, DateTimeOffset.UtcNow.Minute, DateTimeOffset.UtcNow.Second, TimeSpan.Zero) };
    CloudApiFactory _factory = null!;
    HttpClient _client = null!;
    LoginResponse _admin = null!;

    static readonly SubmitNestJobRequest Request = new(
        [new NestPartDto("A", 600, 400, true, "MDF", 18), new NestPartDto("B", 350, 250, false, "MDF", 18)],
        1220, 2440, 12, 15, true);

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;
        await pg.ResetAsync();
        _factory = new CloudApiFactory(pg.ConnectionString, _clock);
        _client = _factory.CreateClient();
        var response = await _client.PostJsonAsync(ApiRoutes.AuthLogin,
            new LoginRequest(CloudApiFactory.Tenant, CloudApiFactory.AdminEmail, CloudApiFactory.AdminPassword, Guid.NewGuid().ToString("D"), "ADMIN-PC"));
        _admin = await response.ReadAsync<LoginResponse>();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    async Task<Guid> SubmitAsync(string key, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.JobsNest)
        {
            Content = new StringContent(CloudJson.Serialize(Request), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadAsync<SubmitNestJobResponse>()).JobId;
    }

    async Task<HttpResponseMessage> GetDiagnosticsAsync(Guid jobId, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.ForAdminJobDiagnostics(jobId));
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await _client.SendAsync(request);
    }

    async Task RunWorkerOnceAsync()
    {
        await using var db = pg.CreateContext();
        var executor = new NestJobExecutor(new PostgresJobRepository(db, _clock), _factory.ObjectStore, new NestingRunner(), db, _clock,
            new WorkerOptions { WorkerId = "diag-worker" }, NullLogger<NestJobExecutor>.Instance);
        Assert.True(await executor.ExecuteOneAsync(CancellationToken.None));
    }

    async Task<string> IssueTokenAsync(string role, Guid? tenantId = null, string? tenantName = null)
    {
        await using var db = pg.CreateContext();
        Guid tid;
        if (tenantId is null)
        {
            var tenant = new TenantEntity { Id = Guid.CreateVersion7(), Name = tenantName ?? "tenant-" + Guid.NewGuid().ToString("N")[..6], CreatedAtUtc = _clock.Now };
            db.Add(tenant);
            tid = tenant.Id;
        }
        else
        {
            tid = tenantId.Value;
        }
        var user = new UserEntity { Id = Guid.CreateVersion7(), TenantId = tid, Email = $"{role}-{Guid.NewGuid():N}@test", PasswordHash = "", Role = role, CreatedAtUtc = _clock.Now };
        user.PasswordHash = new PasswordHasher<UserEntity>().HashPassword(user, "test-only-password");
        var device = new DeviceEntity { Id = Guid.CreateVersion7(), TenantId = tid, UserId = user.Id, DeviceKey = Guid.NewGuid().ToString("D"), CreatedAtUtc = _clock.Now };
        db.AddRange(user, device);
        await db.SaveChangesAsync();
        return _factory.Services.GetRequiredService<AccessTokenIssuer>().Issue(new AccessTokenSubject(user.Id, tid, device.Id, role));
    }

    [PostgresFact]
    public async Task Admin_can_explain_a_finished_job_from_its_id_alone()
    {
        var jobId = await SubmitAsync("diag-1", _admin.AccessToken);
        await RunWorkerOnceAsync();

        var response = await GetDiagnosticsAsync(jobId, _admin.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var diag = CloudJson.Deserialize<JobDiagnosticsResponse>(json);
        Assert.Equal(jobId, diag.JobId);
        Assert.Equal(Guid.Parse(_admin.TenantId), diag.TenantId);
        Assert.Equal(CloudApiFactory.Tenant, diag.TenantName);
        Assert.Equal(CloudApiFactory.AdminEmail, diag.UserEmail);
        Assert.Equal("ADMIN-PC", diag.DeviceName);
        Assert.Equal(JobStatus.Succeeded, diag.Status);
        Assert.Equal("diag-1", diag.IdempotencyKey);
        Assert.Equal(1, diag.AttemptCount);
        Assert.Null(diag.LockedBy);
        Assert.Equal(64, diag.InputSha256.Length);
        Assert.Equal(64, diag.ResultSha256?.Length);
        Assert.Equal(EngineVersion.Current, diag.EngineVersion);
        Assert.NotNull(diag.DurationMs);
        Assert.NotNull(diag.StartedAtUtc);
        Assert.NotNull(diag.CompletedAtUtc);
        Assert.Equal(["job.submitted", "job.claimed", "job.started", "job.succeeded"], diag.AuditEvents.Select(a => a.EventType));
        Assert.All(diag.AuditEvents, a => Assert.Equal(diag.CorrelationId, a.CorrelationId));
        Assert.Contains(diag.AuditEvents, a => a.DetailsJson is not null && a.DetailsJson.Contains("durationMs"));

        // Nothing credential-like, ever.
        await using var db = pg.CreateContext();
        var adminHash = await db.Users.Where(u => u.Email == CloudApiFactory.AdminEmail).Select(u => u.PasswordHash).SingleAsync();
        var tokenHashes = await db.RefreshTokens.Select(t => t.TokenHash).ToListAsync();
        Assert.DoesNotContain(adminHash, json);
        Assert.DoesNotContain(_admin.RefreshToken, json);
        Assert.DoesNotContain(_admin.AccessToken, json);
        Assert.All(tokenHashes, h => Assert.DoesNotContain(h, json));
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Operators_are_refused()
    {
        var jobId = await SubmitAsync("diag-2", _admin.AccessToken);
        var operatorToken = await IssueTokenAsync("operator", Guid.Parse(_admin.TenantId));

        var response = await GetDiagnosticsAsync(jobId, operatorToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthorized, (await response.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Admins_of_other_tenants_see_nothing()
    {
        var jobId = await SubmitAsync("diag-3", _admin.AccessToken);
        var otherAdmin = await IssueTokenAsync("admin");

        var response = await GetDiagnosticsAsync(jobId, otherAdmin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ApiErrorCodes.JobNotFound, (await response.ReadErrorAsync()).Code);
    }

    [PostgresFact]
    public async Task Unknown_job_and_missing_token_are_reported_plainly()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await GetDiagnosticsAsync(Guid.NewGuid(), _admin.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetDiagnosticsAsync(Guid.NewGuid(), null)).StatusCode);
    }

    [PostgresFact]
    public async Task Failed_and_in_flight_jobs_expose_error_and_lease_state()
    {
        var jobId = await SubmitAsync("diag-4", _admin.AccessToken);
        await using (var db = pg.CreateContext())
        {
            var lease = await new PostgresJobRepository(db, _clock).TryClaimNextAsync("stuck-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.Equal(jobId, lease!.Job.Id);
        }

        var running = CloudJson.Deserialize<JobDiagnosticsResponse>(await (await GetDiagnosticsAsync(jobId, _admin.AccessToken)).Content.ReadAsStringAsync());
        Assert.Equal(JobStatus.Running, running.Status);
        Assert.Equal("stuck-worker", running.LockedBy);
        Assert.Equal(_clock.Now.AddMinutes(5), running.LockedUntilUtc);

        await using (var db = pg.CreateContext())
        {
            await new PostgresJobRepository(db, _clock).MarkFailedAsync(jobId, "stuck-worker", ApiErrorCodes.ComputeFailed, "engine said no", retryable: false, CancellationToken.None);
        }

        var failed = CloudJson.Deserialize<JobDiagnosticsResponse>(await (await GetDiagnosticsAsync(jobId, _admin.AccessToken)).Content.ReadAsStringAsync());
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(ApiErrorCodes.ComputeFailed, failed.ErrorCode);
        Assert.Equal("engine said no", failed.ErrorMessage);
        Assert.Null(failed.LockedBy);
    }
}
