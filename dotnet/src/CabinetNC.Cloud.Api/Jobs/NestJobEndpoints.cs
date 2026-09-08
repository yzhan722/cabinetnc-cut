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
            {
                var envelope = await service.GetResultAsync(jobId, context.User, ct);
                if (envelope.V2 is not null) return Results.Json(envelope.V2, CloudJson.Options);
                if (envelope.Operations is not null) return Results.Json(envelope.Operations, CloudJson.Options);
                if (envelope.Post is not null) return Results.Json(envelope.Post, CloudJson.Options);
                return Results.Json(envelope.V1, CloudJson.Options);
            })
            .RequireAuthorization();

        // CAM and post-processor jobs: same idempotency, storage and polling model as nesting.
        endpoints.MapPost(ApiRoutes.JobsOperations, async (
                SubmitOperationsJobRequest request,
                NestJobService service,
                HttpContext context,
                CancellationToken ct) =>
            {
                var response = await service.SubmitOperationsAsync(request, RequireIdempotencyKey(context), context.User, context.GetCorrelationId(), ct);
                return Results.Json(response, CloudJson.Options, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024 * 1024))
            .RequireAuthorization();

        endpoints.MapPost(ApiRoutes.JobsPost, async (
                SubmitPostJobRequest request,
                NestJobService service,
                HttpContext context,
                CancellationToken ct) =>
            {
                var response = await service.SubmitPostAsync(request, RequireIdempotencyKey(context), context.User, context.GetCorrelationId(), ct);
                return Results.Json(response, CloudJson.Options, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(new RequestSizeLimitAttribute(32 * 1024 * 1024))
            .RequireAuthorization();

        // v2: true-shape request. Outlines make bodies larger than v1; 500 panels × 5000 points is still far below 16 MiB.
        endpoints.MapPost(ApiRoutes.JobsNestV2, async (
                SubmitNestJobRequestV2 request,
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
                var response = await service.SubmitV2Async(request, keys[0] ?? "", context.User, context.GetCorrelationId(), ct);
                return Results.Json(response, CloudJson.Options, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024 * 1024))
            .RequireAuthorization();

        return endpoints;
    }

    static string RequireIdempotencyKey(HttpContext context)
    {
        var keys = context.Request.Headers[ApiHeaders.IdempotencyKey];
        if (keys.Count != 1)
        {
            throw new ApiProblemException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequest,
                "Exactly one Idempotency-Key header is required.");
        }
        return keys[0] ?? "";
    }
}
