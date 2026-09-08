namespace CabinetNC.Cloud.Contracts;

public static class ApiRoutes
{
    public const string AuthLogin = "/api/v1/auth/login";
    public const string AuthRefresh = "/api/v1/auth/refresh";
    public const string AuthLogout = "/api/v1/auth/logout";
    public const string JobsNest = "/api/v1/jobs/nest";
    /// <summary>True-shape contract (outlines, cutouts, full stock queue, settings); result is <see cref="NestJobResultV2"/>.</summary>
    public const string JobsNestV2 = "/api/v1/jobs/nest/v2";
    public const string JobStatusTemplate = "/api/v1/jobs/{jobId}";
    public const string JobResultTemplate = "/api/v1/jobs/{jobId}/result";
    public const string Health = "/api/v1/health";
    /// <summary>Readiness: verifies PostgreSQL and the object store; 503 with per-dependency states when not ready.</summary>
    public const string HealthReady = "/api/v1/health/ready";

    public static string ForJobStatus(Guid jobId) => $"/api/v1/jobs/{jobId:D}";
    public static string ForJobResult(Guid jobId) => $"/api/v1/jobs/{jobId:D}/result";

    /// <summary>Admin-only: everything known about a job, so support can work from a JobId alone.</summary>
    public const string AdminJobDiagnosticsTemplate = "/api/v1/admin/jobs/{jobId}/diagnostics";
    public static string ForAdminJobDiagnostics(Guid jobId) => $"/api/v1/admin/jobs/{jobId:D}/diagnostics";

    /// <summary>Self-service password change for the signed-in user.</summary>
    public const string AuthPassword = "/api/v1/auth/password";

    /// <summary>Admin-only tenant administration (users and devices of the caller's tenant).</summary>
    public const string AdminUsers = "/api/v1/admin/users";
    public const string AdminUserTemplate = "/api/v1/admin/users/{userId}";
    public const string AdminUserPasswordTemplate = "/api/v1/admin/users/{userId}/password";
    public const string AdminUserRevokeTemplate = "/api/v1/admin/users/{userId}/revoke";
    public const string AdminDevices = "/api/v1/admin/devices";
    public const string AdminDeviceRevokeTemplate = "/api/v1/admin/devices/{deviceId}/revoke";
    public static string ForAdminUser(Guid userId) => $"/api/v1/admin/users/{userId:D}";
    public static string ForAdminUserPassword(Guid userId) => $"/api/v1/admin/users/{userId:D}/password";
    public static string ForAdminUserRevoke(Guid userId) => $"/api/v1/admin/users/{userId:D}/revoke";
    public static string ForAdminDeviceRevoke(Guid deviceId) => $"/api/v1/admin/devices/{deviceId:D}/revoke";
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
    public const string NestV2 = "nest.v2";
}
