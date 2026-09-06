using System.Net;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>The server answered with an <see cref="ApiError"/> (or an unexpected non-success status).</summary>
public sealed class CloudApiException(HttpStatusCode statusCode, ApiError? error, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public ApiError? Error { get; } = error;
    public string? Code => Error?.Code;
}

/// <summary>No usable session: never logged in, logged out, or the refresh token was rejected.</summary>
public sealed class CloudAuthenticationRequiredException(string message) : Exception(message);

/// <summary>The server could not be reached within the configured grace period.</summary>
public sealed class ComputeUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The job ran and the server reported a final failure.</summary>
public sealed class ComputeJobFailedException(Guid jobId, string? errorCode, string message) : Exception(message)
{
    public Guid JobId { get; } = jobId;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>The job did not finish within <see cref="CloudClientOptions.JobTimeout"/>.</summary>
public sealed class ComputeJobTimeoutException(Guid jobId, TimeSpan waited)
    : Exception($"Job {jobId} did not finish within {waited.TotalSeconds:0} s.")
{
    public Guid JobId { get; } = jobId;
    public TimeSpan Waited { get; } = waited;
}
