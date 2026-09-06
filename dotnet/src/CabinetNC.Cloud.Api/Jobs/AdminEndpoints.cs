using System.Security.Claims;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CabinetNC.Cloud.Api.Jobs;

/// <summary>
/// Support endpoints. Admin role required, always scoped to the caller's tenant. The response is
/// composed field by field so no entity (and therefore no hash column) is ever serialized directly.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.AdminJobDiagnosticsTemplate, GetJobDiagnosticsAsync)
            .RequireAuthorization(policy => policy.RequireRole("admin"));
        return app;
    }

    static async Task<IResult> GetJobDiagnosticsAsync(Guid jobId, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct)
    {
        var tenantId = Guid.Parse(user.FindFirstValue("tenant_id")!);

        var job = await db.ComputeJobs.AsNoTracking()
            .Where(j => j.Id == jobId && j.TenantId == tenantId)
            .Select(j => new
            {
                Job = j,
                TenantName = db.Tenants.Where(t => t.Id == j.TenantId).Select(t => t.Name).First(),
                UserEmail = db.Users.Where(u => u.Id == j.UserId).Select(u => u.Email).First(),
                Device = db.Devices.Where(d => d.Id == j.DeviceId).Select(d => new { d.DeviceKey, d.DeviceName }).First(),
            })
            .SingleOrDefaultAsync(ct);
        if (job is null)
            throw new ApiProblemException(StatusCodes.Status404NotFound, ApiErrorCodes.JobNotFound, "The job does not exist.");

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(a => a.JobId == jobId && a.TenantId == tenantId)
            .OrderBy(a => a.Id)
            .Select(a => new AuditEventDto(a.Id, a.EventType, a.CreatedAtUtc, a.CorrelationId, a.DetailsJson))
            .ToListAsync(ct);

        var j = job.Job;
        return Results.Json(new JobDiagnosticsResponse(
            j.Id, j.TenantId, job.TenantName, j.UserId, job.UserEmail, j.DeviceId, job.Device.DeviceKey, job.Device.DeviceName,
            j.JobType, j.Status, j.CorrelationId, j.IdempotencyKey,
            j.InputObjectKey, j.InputSha256, j.ResultObjectKey, j.ResultSha256, j.EngineVersion,
            j.AttemptCount, j.LockedBy, j.LockedUntilUtc,
            j.CreatedAtUtc, j.InputStoredAtUtc, j.StartedAtUtc, j.CompletedAtUtc,
            j.DurationMs, j.ErrorCode, j.ErrorMessage,
            audit), CloudJson.Options);
    }
}
