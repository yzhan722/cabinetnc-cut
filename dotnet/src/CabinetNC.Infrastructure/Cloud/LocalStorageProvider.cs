namespace CabinetNC.Infrastructure.Cloud;

/// <summary>Mirrors the bucket layout onto a local directory — dev / offline stand-in.</summary>
public sealed class LocalStorageProvider : ICloudStorage
{
    readonly string _rootDir;

    public LocalStorageProvider(string rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            throw new CloudStorageException("local provider needs a root dir", provider: "local");
        _rootDir = rootDir;
    }

    string FileFor(string key)
    {
        if (!CloudPaths.IsSafeRelKey(key))
            throw new CloudStorageException($"unsafe key: {key}", provider: "local");
        return Path.Combine([_rootDir, .. CloudPaths.JoinKey(key).Split('/')]);
    }

    public async Task<CloudObjectInfo> UploadAsync(string key, Stream data, string? contentType = null, CancellationToken ct = default)
    {
        var file = FileFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = $"{file}.tmp-{Environment.ProcessId}";
        try
        {
            await using (var fs = File.Create(tmp))
                await data.CopyToAsync(fs, ct);
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return new CloudObjectInfo { Key = key, Size = new FileInfo(file).Length, Updated = DateTimeOffset.UtcNow };
    }

    public Task<byte[]> DownloadAsync(string key, CancellationToken ct = default)
    {
        var file = FileFor(key);
        if (!File.Exists(file))
            throw new CloudStorageException($"not found: {key}", status: 404, provider: "local");
        return File.ReadAllBytesAsync(file, ct);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(FileFor(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var file = FileFor(key);
        if (File.Exists(file)) File.Delete(file);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string prefix = "", CancellationToken ct = default)
    {
        var baseDir = Path.Combine([_rootDir, .. CloudPaths.JoinKey(prefix).Split('/')]);
        var list = new List<CloudObjectInfo>();
        if (Directory.Exists(baseDir))
        {
            foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                list.Add(new CloudObjectInfo
                {
                    Key = Path.GetRelativePath(_rootDir, f).Replace('\\', '/'),
                    Size = fi.Length,
                    Updated = fi.LastWriteTimeUtc,
                });
            }
        }
        return Task.FromResult<IReadOnlyList<CloudObjectInfo>>(list);
    }
}
