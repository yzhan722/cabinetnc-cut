namespace CabinetNC.Infrastructure.Cloud;

/// <summary>
/// App-facing cloud facade — same contract as the JS layer (src/cloud/index.js,
/// the-cab-lab/cloud/index.js). OmniCam is local-first: project.db / exported
/// files are the source of truth and cloud sync only mirrors completed writes.
/// Every helper catches provider errors into SyncResult — nothing here may
/// break a local save.
/// </summary>
public sealed class CloudSync
{
    public sealed record SyncResult(bool Ok, string? Key = null, string? Error = null, long Ms = 0);
    public sealed record Status(
        bool Enabled, string Provider, string? Bucket, string? ProjectId,
        string Region, string Root, bool Ready, string? Error, SyncResult? LastSync);

    readonly CloudStorageConfig _config;
    readonly Lazy<ICloudStorage?> _provider;
    string? _providerError;
    SyncResult? _lastSync;

    static readonly Lazy<CloudSync> _shared = new(() => new CloudSync(CloudStorageConfig.FromEnvironment()));

    /// <summary>Process-wide instance built from environment config.</summary>
    public static CloudSync Shared => _shared.Value;

    public CloudSync(CloudStorageConfig config)
    {
        _config = config;
        _provider = new Lazy<ICloudStorage?>(() =>
        {
            try
            {
                return CreateProvider(config);
            }
            catch (Exception ex)
            {
                _providerError = ex.Message;
                return null;
            }
        });
    }

    static ICloudStorage? CreateProvider(CloudStorageConfig cfg)
    {
        if (!cfg.Enabled) return null;
        return cfg.Provider switch
        {
            "local" => new LocalStorageProvider(cfg.LocalRoot),
            "gcp" => new GcpStorageProvider(cfg),
            _ => throw new CloudStorageException($"unknown provider: {cfg.Provider}"),
        };
    }

    public Status GetStatus() => new(
        _config.Enabled, _config.Provider, _config.GcpBucket, _config.GcpProjectId,
        _config.GcpRegion, _config.Root, _provider.Value is not null, _providerError, _lastSync);

    async Task<SyncResult> SafeAsync(Func<ICloudStorage, CancellationToken, Task<string?>> op, CancellationToken ct)
    {
        var p = _provider.Value;
        if (p is null) return new SyncResult(false, Error: "cloud disabled");
        var t0 = Environment.TickCount64;
        try
        {
            var key = await op(p, ct);
            return _lastSync = new SyncResult(true, Key: key, Ms: Environment.TickCount64 - t0);
        }
        catch (Exception ex)
        {
            return _lastSync = new SyncResult(false, Error: ex.Message, Ms: Environment.TickCount64 - t0);
        }
    }

    /// <summary>Mirror a local file into the bucket under `root`.</summary>
    public Task<SyncResult> UploadFileAsync(string root, string rel, string localPath, string? contentType = null, CancellationToken ct = default) =>
        SafeAsync(async (p, _) =>
        {
            var key = CloudPaths.CloudKey(root, rel);
            await using var fs = File.OpenRead(localPath);
            await p.UploadAsync(key, fs, contentType, ct);
            return key;
        }, ct);

    public Task<SyncResult> UploadTextAsync(string root, string rel, string text, CancellationToken ct = default) =>
        SafeAsync(async (p, _) =>
        {
            var key = CloudPaths.CloudKey(root, rel);
            await p.UploadAsync(key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), "text/plain; charset=utf-8", ct);
            return key;
        }, ct);

    public async Task<SyncResult<string?>> DownloadTextAsync(string root, string rel, CancellationToken ct = default)
    {
        var p = _provider.Value;
        if (p is null) return new SyncResult<string?>(false, null, "cloud disabled");
        var key = CloudPaths.CloudKey(root, rel);
        try
        {
            var bytes = await p.DownloadAsync(key, ct);
            return new SyncResult<string?>(true, System.Text.Encoding.UTF8.GetString(bytes), null);
        }
        catch (Exception ex)
        {
            return new SyncResult<string?>(false, null, ex.Message);
        }
    }

    public Task<SyncResult> DeleteAsync(string root, string rel, CancellationToken ct = default) =>
        SafeAsync(async (p, _) =>
        {
            var key = CloudPaths.CloudKey(root, rel);
            await p.DeleteAsync(key, ct);
            return key;
        }, ct);

    public async Task<(bool Ok, IReadOnlyList<CloudObjectInfo> Items, string? Error)> ListAsync(string root, string rel = "", CancellationToken ct = default)
    {
        var p = _provider.Value;
        if (p is null) return (false, [], "cloud disabled");
        var prefix = CloudPaths.JoinKey(root, rel);
        if (prefix.Length > 0)
        {
            if (!CloudPaths.Roots.Contains(prefix.Split('/')[0]))
                return (false, [], $"list prefix must start with {string.Join("|", CloudPaths.Roots)}: {prefix}");
            prefix += "/";
        }
        try
        {
            return (true, await p.ListAsync(prefix, ct), null);
        }
        catch (Exception ex)
        {
            return (false, [], ex.Message);
        }
    }
}

public sealed record SyncResult<T>(bool Ok, T? Value, string? Error);
