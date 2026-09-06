using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Api.Http;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string ItemKey = "CabinetNC.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[ApiHeaders.CorrelationId];
        var supplied = values.Count == 1 ? values[0] : null;
        var correlationId = TryNormalize(supplied, out var normalized)
            ? normalized
            : Guid.CreateVersion7().ToString("D");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[ApiHeaders.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        await next(context);
    }

    static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (!Guid.TryParseExact(value, "D", out var id))
            return false;
        normalized = id.ToString("D");
        return true;
    }
}

public static class CorrelationIdHttpContextExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value)
            ? value as string ?? ""
            : "";
}
