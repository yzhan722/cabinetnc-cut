using System.Text.Json;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Api.Http;

public static class ApiResponse
{
    public static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken ct = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var error = new ApiError(code, message, context.GetCorrelationId());
        return JsonSerializer.SerializeAsync(context.Response.Body, error, CloudJson.Options, ct);
    }
}

public sealed class ApiProblemException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
