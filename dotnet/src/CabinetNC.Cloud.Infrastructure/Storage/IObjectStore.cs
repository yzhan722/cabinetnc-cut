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
    /// <summary>host[:port] without scheme, e.g. <c>minio:9000</c>.</summary>
    public required string Endpoint { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public string Bucket { get; init; } = "cabinetnc";
    public bool UseSsl { get; init; }
    public string? Region { get; init; }
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
