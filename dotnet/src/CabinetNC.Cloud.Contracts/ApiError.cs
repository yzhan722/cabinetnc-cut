namespace CabinetNC.Cloud.Contracts;

/// <summary>Uniform error body for every non-2xx response. Never carries a stack trace.</summary>
public sealed record ApiError(string Code, string Message, string CorrelationId);

/// <summary>Stable machine-readable codes from the design spec §12. Desktop switches on these.</summary>
public static class ApiErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidDevice = "invalid_device";
    public const string TokenExpired = "token_expired";
    public const string RefreshInvalid = "refresh_invalid";
    public const string RefreshReuseDetected = "refresh_reuse_detected";
    public const string Unauthorized = "unauthorized";
    public const string InvalidRequest = "invalid_request";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string JobNotFound = "job_not_found";
    public const string JobNotReady = "job_not_ready";
    public const string ComputeFailed = "compute_failed";
    public const string StorageFailed = "storage_failed";

    public static IReadOnlyList<string> All { get; } =
    [
        InvalidCredentials,
        InvalidDevice,
        TokenExpired,
        RefreshInvalid,
        RefreshReuseDetected,
        Unauthorized,
        InvalidRequest,
        IdempotencyConflict,
        JobNotFound,
        JobNotReady,
        ComputeFailed,
        StorageFailed,
    ];
}
