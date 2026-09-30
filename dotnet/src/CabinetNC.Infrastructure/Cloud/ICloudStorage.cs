namespace CabinetNC.Infrastructure.Cloud;

public sealed class CloudObjectInfo
{
    public required string Key { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Updated { get; init; }
}

public sealed class CloudStorageException : Exception
{
    public int? Status { get; }
    public string? Provider { get; }

    public CloudStorageException(string message, int? status = null, string? provider = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Provider = provider;
    }
}

/// <summary>
/// StorageProvider contract — same surface as the JS layer in both apps:
///   upload / download / exists / delete / list.
/// Business code only sees this interface; remote failures throw
/// CloudStorageException and callers that mirror local work must catch and log
/// (local-first: a cloud failure never breaks a local save).
/// </summary>
public interface ICloudStorage
{
    Task<CloudObjectInfo> UploadAsync(string key, Stream data, string? contentType = null, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default); // missing is not an error
    Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string prefix = "", CancellationToken ct = default);
}
