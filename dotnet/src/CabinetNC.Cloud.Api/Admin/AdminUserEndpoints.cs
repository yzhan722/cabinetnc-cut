using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace CabinetNC.Cloud.Api.Admin;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("").RequireAuthorization(policy => policy.RequireRole(UserRoles.Admin));

        admin.MapGet(ApiRoutes.AdminUsers, async (TenantAdminService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.ListUsersAsync(TenantAdminService.Actor.From(context.User), ct), CloudJson.Options));

        admin.MapPost(ApiRoutes.AdminUsers, async ([FromBody] CreateUserRequest request, TenantAdminService service, HttpContext context, CancellationToken ct) =>
        {
            var created = await service.CreateUserAsync(request, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct);
            return Results.Json(created, CloudJson.Options, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        admin.MapPatch(ApiRoutes.AdminUserTemplate, async (Guid userId, [FromBody] UpdateUserRequest request, TenantAdminService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.UpdateUserAsync(userId, request, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct), CloudJson.Options))
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        admin.MapPost(ApiRoutes.AdminUserPasswordTemplate, async (Guid userId, [FromBody] ResetPasswordRequest request, TenantAdminService service, HttpContext context, CancellationToken ct) =>
        {
            await service.ResetPasswordAsync(userId, request, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct);
            return Results.NoContent();
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        admin.MapPost(ApiRoutes.AdminUserRevokeTemplate, async (Guid userId, TenantAdminService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.RevokeUserSessionsAsync(userId, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct), CloudJson.Options));

        admin.MapGet(ApiRoutes.AdminDevices, async (Guid? userId, TenantAdminService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.ListDevicesAsync(userId, TenantAdminService.Actor.From(context.User), ct), CloudJson.Options));

        admin.MapPost(ApiRoutes.AdminDeviceRevokeTemplate, async (Guid deviceId, TenantAdminService service, HttpContext context, CancellationToken ct) =>
            Results.Json(await service.RevokeDeviceAsync(deviceId, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct), CloudJson.Options));

        // Any signed-in user; rate limited like login because it verifies a password.
        app.MapPost(ApiRoutes.AuthPassword, async ([FromBody] ChangePasswordRequest request, TenantAdminService service, HttpContext context, CancellationToken ct) =>
        {
            await service.ChangeOwnPasswordAsync(request, TenantAdminService.Actor.From(context.User), context.GetCorrelationId(), ct);
            return Results.NoContent();
        })
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .RequireAuthorization()
            .RequireRateLimiting("login");

        return app;
    }
}
