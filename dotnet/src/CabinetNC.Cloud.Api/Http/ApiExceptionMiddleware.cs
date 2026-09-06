using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Api.Http;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller disconnected; there is no response to write.
        }
        catch (ApiProblemException ex) when (!context.Response.HasStarted)
        {
            await ApiResponse.WriteErrorAsync(
                context, ex.StatusCode, ex.Code, ex.Message, context.RequestAborted);
        }
        catch (BadHttpRequestException) when (!context.Response.HasStarted)
        {
            await ApiResponse.WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidRequest,
                "The request body is invalid.",
                context.RequestAborted);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            // Log the type, not Exception.Message/ToString: provider exceptions may echo connection
            // strings. The public response and the log still join through the correlation id.
            logger.LogError(
                "Unhandled {ExceptionType}; correlationId={CorrelationId}",
                ex.GetType().FullName,
                context.GetCorrelationId());
            await ApiResponse.WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError,
                "An internal error occurred.",
                context.RequestAborted);
        }
    }
}
