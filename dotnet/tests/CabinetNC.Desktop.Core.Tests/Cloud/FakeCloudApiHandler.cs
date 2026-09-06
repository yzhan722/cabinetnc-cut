using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

/// <summary>
/// In-memory stand-in for the intranet API, faithful to the wire contracts: rotating refresh tokens
/// with reuse detection, access-token expiry, idempotent job submission, a status sequence per job,
/// and injectable network failures. Lets the client be tested without a server.
/// </summary>
public sealed class FakeCloudApiHandler : HttpMessageHandler
{
    public const string Password = "correct-password";
    public const string TenantId = "0f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a";
    public const string UserId = "1f1c2b9e-0d5a-4c6e-9a1b-2f3e4d5c6b7a";

    readonly object _gate = new();
    readonly HashSet<string> _validAccess = new(StringComparer.Ordinal);
    readonly HashSet<string> _expiredAccess = new(StringComparer.Ordinal);
    readonly HashSet<string> _activeRefresh = new(StringComparer.Ordinal);
    readonly HashSet<string> _revokedRefresh = new(StringComparer.Ordinal);
    readonly Dictionary<string, Guid> _jobsByIdempotencyKey = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, Queue<JobStatus>> _jobStatusSequence = [];
    readonly Dictionary<Guid, JobStatus> _jobCurrent = [];
    int _counter;

    public List<string> RequestLog { get; } = [];
    public List<string> SubmittedIdempotencyKeys { get; } = [];
    public List<string> LoginDeviceIds { get; } = [];
    public int LoginCount { get; private set; }
    public int RefreshCount { get; private set; }
    public int LogoutCount { get; private set; }
    public int AccessTokenLifetimeSeconds { get; set; } = 900;

    /// <summary>Status sequence handed out on successive polls for every new job (last value repeats).</summary>
    public JobStatus[] StatusSequence { get; set; } = [JobStatus.Queued, JobStatus.Running, JobStatus.Succeeded];
    public string? FailedJobErrorCode { get; set; } = ApiErrorCodes.ComputeFailed;

    /// <summary>Next N requests matching the predicate throw <see cref="HttpRequestException"/> (network loss).</summary>
    public int NetworkFailuresRemaining { get; set; }
    public Func<HttpRequestMessage, bool> NetworkFailureFilter { get; set; } = _ => true;

    /// <summary>When true every refresh is rejected as invalid (e.g. server-side revocation).</summary>
    public bool RejectRefresh { get; set; }

    public string? LastIssuedAccessToken { get; private set; }
    public string? LastIssuedRefreshToken { get; private set; }

    public void ExpireAccessToken(string token)
    {
        lock (_gate)
        {
            _validAccess.Remove(token);
            _expiredAccess.Add(token);
        }
    }

    public void ExpireAllAccessTokens()
    {
        lock (_gate)
        {
            foreach (var token in _validAccess)
                _expiredAccess.Add(token);
            _validAccess.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Yield();
        var path = request.RequestUri!.AbsolutePath;
        lock (_gate)
            RequestLog.Add($"{request.Method} {path}");

        if (NetworkFailuresRemaining > 0 && NetworkFailureFilter(request))
        {
            NetworkFailuresRemaining--;
            throw new HttpRequestException("simulated network loss");
        }

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        return (request.Method.Method, path) switch
        {
            ("GET", ApiRoutes.Health) => Json(HttpStatusCode.OK, new HealthResponse("ok", "fake", "0", DateTimeOffset.UtcNow)),
            ("POST", ApiRoutes.AuthLogin) => Login(body),
            ("POST", ApiRoutes.AuthRefresh) => Refresh(body),
            ("POST", ApiRoutes.AuthLogout) => Authorized(request, _ => { LogoutCount++; return new HttpResponseMessage(HttpStatusCode.NoContent); }),
            ("POST", ApiRoutes.JobsNest) => Authorized(request, _ => Submit(request, body)),
            _ when request.Method == HttpMethod.Get && path.StartsWith("/api/v1/jobs/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal)
                => Authorized(request, _ => Result(Guid.Parse(path.Split('/')[4]))),
            _ when request.Method == HttpMethod.Get && path.StartsWith("/api/v1/jobs/", StringComparison.Ordinal)
                => Authorized(request, _ => Status(Guid.Parse(path.Split('/')[4]))),
            _ => Error(HttpStatusCode.NotFound, ApiErrorCodes.InvalidRequest, "not found"),
        };
    }

    HttpResponseMessage Login(string body)
    {
        var login = CloudJson.Deserialize<LoginRequest>(body);
        lock (_gate)
        {
            LoginCount++;
            LoginDeviceIds.Add(login.DeviceId);
        }
        if (login.Password != Password)
            return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.InvalidCredentials, "bad credentials");
        var (access, refresh) = Issue();
        return Json(HttpStatusCode.OK, new LoginResponse(access, AccessTokenLifetimeSeconds, refresh, 30 * 24 * 3600, TenantId, UserId, login.DeviceId, "operator"));
    }

    HttpResponseMessage Refresh(string body)
    {
        var refresh = CloudJson.Deserialize<RefreshRequest>(body);
        lock (_gate)
        {
            RefreshCount++;
            if (RejectRefresh)
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.RefreshInvalid, "refresh rejected");
            if (_revokedRefresh.Contains(refresh.RefreshToken))
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.RefreshReuseDetected, "reuse");
            if (!_activeRefresh.Remove(refresh.RefreshToken))
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.RefreshInvalid, "unknown token");
            _revokedRefresh.Add(refresh.RefreshToken);
        }
        var (access, next) = Issue();
        return Json(HttpStatusCode.OK, new RefreshResponse(access, AccessTokenLifetimeSeconds, next, 30 * 24 * 3600));
    }

    (string Access, string Refresh) Issue()
    {
        lock (_gate)
        {
            var n = ++_counter;
            var access = $"access-{n}";
            var refresh = $"refresh-{n}";
            _validAccess.Add(access);
            _activeRefresh.Add(refresh);
            LastIssuedAccessToken = access;
            LastIssuedRefreshToken = refresh;
            return (access, refresh);
        }
    }

    HttpResponseMessage Authorized(HttpRequestMessage request, Func<string, HttpResponseMessage> action)
    {
        var token = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;
        lock (_gate)
        {
            if (token is null)
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized, "no token");
            if (_expiredAccess.Contains(token))
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.TokenExpired, "expired");
            if (!_validAccess.Contains(token))
                return Error(HttpStatusCode.Unauthorized, ApiErrorCodes.Unauthorized, "unknown token");
        }
        return action(token);
    }

    HttpResponseMessage Submit(HttpRequestMessage request, string body)
    {
        if (!request.Headers.TryGetValues(ApiHeaders.IdempotencyKey, out var keys))
            return Error(HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest, "missing idempotency key");
        var key = keys.Single();
        _ = CloudJson.Deserialize<SubmitNestJobRequest>(body);
        lock (_gate)
        {
            SubmittedIdempotencyKeys.Add(key);
            if (!_jobsByIdempotencyKey.TryGetValue(key, out var jobId))
            {
                jobId = Guid.NewGuid();
                _jobsByIdempotencyKey[key] = jobId;
                _jobStatusSequence[jobId] = new Queue<JobStatus>(StatusSequence);
                _jobCurrent[jobId] = JobStatus.Queued;
            }
            return Json(HttpStatusCode.Accepted, new SubmitNestJobResponse(jobId, JobStatus.Queued, "corr"));
        }
    }

    HttpResponseMessage Status(Guid jobId)
    {
        lock (_gate)
        {
            if (!_jobStatusSequence.TryGetValue(jobId, out var sequence))
                return Error(HttpStatusCode.NotFound, ApiErrorCodes.JobNotFound, "no job");
            if (sequence.Count > 1)
                _jobCurrent[jobId] = sequence.Dequeue();
            else if (sequence.Count == 1)
                _jobCurrent[jobId] = sequence.Peek();
            var status = _jobCurrent[jobId];
            return Json(HttpStatusCode.OK, new JobStatusResponse(
                jobId, JobTypes.Nest, status, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                status is JobStatus.Succeeded or JobStatus.Failed ? DateTimeOffset.UtcNow : null,
                status == JobStatus.Failed ? FailedJobErrorCode : null,
                status == JobStatus.Failed ? "engine said no" : null,
                status == JobStatus.Succeeded ? 42 : null,
                "corr"));
        }
    }

    HttpResponseMessage Result(Guid jobId)
    {
        lock (_gate)
        {
            if (!_jobCurrent.TryGetValue(jobId, out var status))
                return Error(HttpStatusCode.NotFound, ApiErrorCodes.JobNotFound, "no job");
            if (status != JobStatus.Succeeded)
                return Error(HttpStatusCode.Conflict, ApiErrorCodes.JobNotReady, "not ready");
            return Json(HttpStatusCode.OK, new NestJobResult(
                jobId, "grouped_blf_v0", "CabinetNC.Compute.Core/test",
                [new NestPlacementDto("A", 0, 15, 15, 0), new NestPlacementDto("B", 0, 627, 15, 90)],
                1, ["BIG"],
                [new NestWarningDto("aabb_gap", "spacing/collision A x B on sheet 0", "A", "B", 0)],
                new string('a', 64), new string('b', 64), 42));
        }
    }

    static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
        new(status) { Content = new StringContent(CloudJson.Serialize(value), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(status, new ApiError(code, message, "corr-fake"));
}
