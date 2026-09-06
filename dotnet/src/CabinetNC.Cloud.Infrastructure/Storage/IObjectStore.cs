namespace CabinetNC.Cloud.Infrastructure.Storage;

/// <summary>
/// Provider-neutral blob storage for job inputs/results (design spec §10). Keys follow
/// <see cref="ObjectKeys"/>; every implementation must reject unsafe keys via <see cref="ObjectKeyRules"/>.
/// </summary>
public interface IObjectStore
{
    Task PutAsync(string key, Stream data, string contentType, CancellationToken ct);

    /// <summary>Returns a readable stream positioned at 0; throws <see cref="ObjectStoreNotFoundException"/> when the key is missing.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);
}

public sealed class ObjectStoreNotFoundException(string key) : Exception($"Object '{key}' was not found in the object store.")
{
    public string Key { get; } = key;
}

public sealed record ObjectStoreOptions
{
    public const string EndpointVariable = "CABINETNC_OBJECTSTORE_ENDPOINT";
    public const string AccessKeyVariable = "CABINETNC_OBJECTSTORE_ACCESS_KEY";
    public const string SecretKeyVariable = "CABINETNC_OBJECTSTORE_SECRET_KEY";
    public const string BucketVariable = "CABINETNC_OBJECTSTORE_BUCKET";
    public const string UseSslVariable = "CABINETNC_OBJECTSTORE_USE_SSL";
    public const string RegionVariable = "CABINETNC_OBJECTSTORE_REGION";

    /// <summary>host[:port] without scheme, e.g. <c>minio:9000</c>.</summary>
    public required string Endpoint { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string Bucket { get; init; } = "cabinetnc";
    public bool UseSsl { get; init; }
    public string? Region { get; init; }

    /// <summary>Production credentials are environment-only; errors name variables but never values.</summary>
    public static ObjectStoreOptions FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        var accessKey = Environment.GetEnvironmentVariable(AccessKeyVariable);
        var secretKey = Environment.GetEnvironmentVariable(SecretKeyVariable);
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException($"{EndpointVariable} is required.");
        if (string.IsNullOrWhiteSpace(accessKey))
            throw new InvalidOperationException($"{AccessKeyVariable} is required.");
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException($"{SecretKeyVariable} is required.");

        var sslText = Environment.GetEnvironmentVariable(UseSslVariable);
        if (!string.IsNullOrWhiteSpace(sslText) && !bool.TryParse(sslText, out _))
            throw new InvalidOperationException($"{UseSslVariable} must be true or false.");

        return new ObjectStoreOptions
        {
            Endpoint = endpoint,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Bucket = Environment.GetEnvironmentVariable(BucketVariable) ?? "cabinetnc",
            UseSsl = bool.TryParse(sslText, out var useSsl) && useSsl,
            Region = Environment.GetEnvironmentVariable(RegionVariable),
        };
    }
}

/// <summary>Rejects anything that is not a plain, relative, slash-separated key.</summary>
public static class ObjectKeyRules
{
    public const int MaxLength = 1024;

    public static void EnsureSafe(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Object key must not be empty.", nameof(key));
        if (key.Length > MaxLength)
            throw new ArgumentException($"Object key exceeds {MaxLength} characters.", nameof(key));
        if (key[0] == '/')
            throw new ArgumentException("Object key must be relative (no leading '/').", nameof(key));
        if (key.Contains('\\'))
            throw new ArgumentException("Object key must use '/' separators (no backslash).", nameof(key));
        if (key.Any(c => c < 0x20 || c == 0x7F))
            throw new ArgumentException("Object key must not contain control characters.", nameof(key));

        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0)
                throw new ArgumentException("Object key must not contain empty segments ('//' or trailing '/').", nameof(key));
            if (segment is "." or "..")
                throw new ArgumentException("Object key must not contain '.' or '..' segments.", nameof(key));
        }
    }
}
