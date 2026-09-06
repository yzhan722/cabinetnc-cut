using System.Net;
using System.Net.Http.Headers;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>
/// Attaches the bearer token and, on a 401 for an expired/unknown token, refreshes once through the
/// session (single-flight across concurrent requests) and replays the request exactly once.
/// </summary>
public sealed class AuthenticatedHttpHandler(AuthSession session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(ct);
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !await IsTokenProblemAsync(response, ct))
            return response;

        response.Dispose();
        var fresh = await session.ForceRefreshAsync(token, ct);
        using var retry = Clone(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, ct);
    }

    static async Task<bool> IsTokenProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = CloudJson.Deserialize<ApiError>(await response.Content.ReadAsStringAsync(ct));
            return error.Code is ApiErrorCodes.TokenExpired or ApiErrorCodes.Unauthorized;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (original.Content is not null)
            {
                foreach (var header in original.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clone;
    }
}
