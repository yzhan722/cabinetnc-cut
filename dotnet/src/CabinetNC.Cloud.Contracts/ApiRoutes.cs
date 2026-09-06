namespace CabinetNC.Cloud.Contracts;

public static class ApiRoutes
{
    public const string AuthLogin = "/api/v1/auth/login";
    public const string AuthRefresh = "/api/v1/auth/refresh";
    public const string AuthLogout = "/api/v1/auth/logout";
    public const string JobsNest = "/api/v1/jobs/nest";
    public const string JobStatusTemplate = "/api/v1/jobs/{jobId}";
    public const string JobResultTemplate = "/api/v1/jobs/{jobId}/result";
    public const string Health = "/api/v1/health";

    public static string ForJobStatus(Guid jobId) => $"/api/v1/jobs/{jobId:D}";
    public static string ForJobResult(Guid jobId) => $"/api/v1/jobs/{jobId:D}/result";
}

public static class ApiHeaders
{
    /// <summary>Required on <see cref="ApiRoutes.JobsNest"/>; same tenant+user+key returns the same JobId.</summary>
    public const string IdempotencyKey = "Idempotency-Key";
    /// <summary>Present on every response; echoed from the request when supplied.</summary>
    public const string CorrelationId = "X-Correlation-ID";
}

public static class JobTypes
{
    public const string Nest = "nest";
}
