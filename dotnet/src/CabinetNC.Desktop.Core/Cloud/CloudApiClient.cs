using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>
/// Typed client for the intranet API. Anonymous calls (health, login, refresh) go straight to the
/// transport; everything else goes through <see cref="AuthenticatedHttpHandler"/>. TLS validation is
/// the platform default — the intranet root certificate is trusted via the Windows store, never bypassed.
/// </summary>
public sealed class CloudApiClient : IDisposable, ICloudAuthTransport
{
    readonly HttpClient _anonymous;
    readonly HttpClient _authenticated;
    readonly HttpMessageHandler? _ownedTransport;

    public CloudApiClient(
        CloudClientOptions options,
        ITokenStore tokenStore,
        DeviceIdentityStore deviceIdentity,
        TimeProvider? clock = null,
        HttpMessageHandler? transport = null)
    {
        Options = options;
        clock ??= TimeProvider.System;
        if (transport is null)
        {
            transport = _ownedTransport = new SocketsHttpHandler
            {
                ConnectTimeout = options.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
        }

        _anonymous = new HttpClient(transport, disposeHandler: false) { BaseAddress = options.BaseAddress, Timeout = options.RequestTimeout };
        Session = new AuthSession(this, tokenStore, deviceIdentity, options, clock);
        // The delegating handler owns nothing; the transport is disposed here only when we created it.
        _authenticated = new HttpClient(new AuthenticatedHttpHandler(Session) { InnerHandler = transport }, disposeHandler: false)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.RequestTimeout,
        };
    }

    public CloudClientOptions Options { get; }
    public AuthSession Session { get; }

    public Task<HealthResponse> GetHealthAsync(CancellationToken ct) =>
        SendAsync<HealthResponse>(_anonymous, HttpMethod.Get, ApiRoutes.Health, null, null, ct);

    public Task<SubmitNestJobResponse> SubmitNestJobAsync(SubmitNestJobRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<SubmitNestJobResponse>(_authenticated, HttpMethod.Post, ApiRoutes.JobsNest, request,
            r => r.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, idempotencyKey), ct);

    public Task<SubmitNestJobResponse> SubmitNestJobV2Async(SubmitNestJobRequestV2 request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<SubmitNestJobResponse>(_authenticated, HttpMethod.Post, ApiRoutes.JobsNestV2, request,
            r => r.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, idempotencyKey), ct);

    public Task<JobStatusResponse> GetJobStatusAsync(Guid jobId, CancellationToken ct) =>
        SendAsync<JobStatusResponse>(_authenticated, HttpMethod.Get, ApiRoutes.ForJobStatus(jobId), null, null, ct);

    public Task<NestJobResult> GetJobResultAsync(Guid jobId, CancellationToken ct) =>
        SendAsync<NestJobResult>(_authenticated, HttpMethod.Get, ApiRoutes.ForJobResult(jobId), null, null, ct);

    /// <summary>Result of a job submitted through <see cref="SubmitNestJobV2Async"/>.</summary>
    public Task<NestJobResultV2> GetJobResultV2Async(Guid jobId, CancellationToken ct) =>
        SendAsync<NestJobResultV2>(_authenticated, HttpMethod.Get, ApiRoutes.ForJobResult(jobId), null, null, ct);

    Task<LoginResponse> ICloudAuthTransport.LoginAsync(LoginRequest request, CancellationToken ct) =>
        SendAsync<LoginResponse>(_anonymous, HttpMethod.Post, ApiRoutes.AuthLogin, request, null, ct);

    Task<RefreshResponse> ICloudAuthTransport.RefreshAsync(RefreshRequest request, CancellationToken ct) =>
        SendAsync<RefreshResponse>(_anonymous, HttpMethod.Post, ApiRoutes.AuthRefresh, request, null, ct);

    async Task ICloudAuthTransport.LogoutAsync(LogoutRequest request, string accessToken, CancellationToken ct)
    {
        using var message = Build(HttpMethod.Post, ApiRoutes.AuthLogout, request);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendRawAsync(_anonymous, message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string path, object? body, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        using var message = Build(method, path, body);
        configure?.Invoke(message);
        using var response = await SendRawAsync(client, message, ct);
        await EnsureSuccessAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return CloudJson.Deserialize<T>(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new CloudApiException(response.StatusCode, null, $"The server returned an unreadable {typeof(T).Name}.");
        }
    }

    static HttpRequestMessage Build(HttpMethod method, string path, object? body)
    {
        var message = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(CloudJson.Serialize(body)));
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        return message;
    }

    static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ComputeUnavailableException("The intranet service could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ComputeUnavailableException("The intranet service did not answer in time.", ex);
        }
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        ApiError? error = null;
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(json))
                error = CloudJson.Deserialize<ApiError>(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Non-API error body (proxy page, etc.); report the status only.
        }
        var message = error is null
            ? $"The intranet service answered {(int)response.StatusCode} {response.ReasonPhrase}."
            : $"{error.Message} [{error.Code}]";
        throw new CloudApiException(response.StatusCode, error, message);
    }

    public void Dispose()
    {
        _authenticated.Dispose();
        _anonymous.Dispose();
        _ownedTransport?.Dispose();
    }
}
