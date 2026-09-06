using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace CabinetNC.Cloud.Api.Jobs;

public static class NestJobEndpoints
{
    public static IEndpointRouteBuilder MapNestJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.JobsNest, async (
                SubmitNestJobRequest request,
                NestJobService service,
                HttpContext context,
                CancellationToken ct) =>
            {
                var keys = context.Request.Headers[ApiHeaders.IdempotencyKey];
                if (keys.Count != 1)
                {
                    throw new ApiProblemException(
                        StatusCodes.Status400BadRequest,
                        ApiErrorCodes.InvalidRequest,
                        "Exactly one Idempotency-Key header is required.");
                }

                var response = await service.SubmitAsync(
                    request,
                    keys[0] ?? "",
                    context.User,
                    context.GetCorrelationId(),
                    ct);
                return Results.Json(response, CloudJson.Options, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(new RequestSizeLimitAttribute(2 * 1024 * 1024))
            .RequireAuthorization();

        endpoints.MapGet(ApiRoutes.JobStatusTemplate, async (
                Guid jobId,
                NestJobService service,
                HttpContext context,
                CancellationToken ct) =>
            Results.Json(
                await service.GetStatusAsync(jobId, context.User, ct),
                CloudJson.Options))
            .RequireAuthorization();

        endpoints.MapGet(ApiRoutes.JobResultTemplate, async (
                Guid jobId,
                NestJobService service,
                HttpContext context,
                CancellationToken ct) =>
            Results.Json(
                await service.GetResultAsync(jobId, context.User, ct),
                CloudJson.Options))
            .RequireAuthorization();

        return endpoints;
    }
}
