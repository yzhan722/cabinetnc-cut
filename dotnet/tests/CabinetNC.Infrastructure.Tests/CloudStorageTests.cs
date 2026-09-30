using System.Text;
using CabinetNC.Infrastructure.Cloud;

namespace CabinetNC.Infrastructure.Tests;

/// <summary>
/// Cloud-storage layer tests. Local/provider-shape coverage is offline; the GCS
/// round trip only runs when CAB_CLOUD_ENABLED=1 + CAB_GCP_BUCKET are set
/// (same convention as scripts/check-cloud.mjs).
/// </summary>
public class CloudStorageTests
{
    static CloudStorageConfig Env() => CloudStorageConfig.FromEnvironment();

    [Fact]
    public void Disabled_config_returns_null_provider()
    {
        var cfg = new CloudStorageConfig
        {
            Enabled = false,
            Provider = "gcp",
            GcpBucket = "anything",
        };
        var sync = new CloudSync(cfg);
        Assert.False(sync.GetStatus().Enabled);
        Assert.False(sync.GetStatus().Ready);
    }

    [Fact]
    public void CloudKey_validates_roots_and_paths()
    {
        Assert.Equal("omnicam/jobs/a.txt", CloudPaths.CloudKey("omnicam", "jobs/a.txt"));
        Assert.Equal("shared/x/y.txt", CloudPaths.CloudKey("shared/x", "y.txt"));
        Assert.Throws<CloudStorageException>(() => CloudPaths.CloudKey("bogus", "a.txt"));
        Assert.Throws<CloudStorageException>(() => CloudPaths.CloudKey("omnicam", "../escape.txt"));
        Assert.Throws<CloudStorageException>(() => CloudPaths.CloudKey("omnicam", "a\\b.txt"));
        Assert.Throws<CloudStorageException>(() => CloudPaths.CloudKey("omnicam", ""));
    }

    [Fact]
    public async Task Local_provider_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-cloud-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new LocalStorageProvider(dir);
            var payload = Encoding.UTF8.GetBytes("hello-δχ");
            await p.UploadAsync("omnicam/jobs/t.txt", new MemoryStream(payload));

            Assert.True(await p.ExistsAsync("omnicam/jobs/t.txt"));
            Assert.False(await p.ExistsAsync("omnicam/jobs/missing.txt"));

            var got = await p.DownloadAsync("omnicam/jobs/t.txt");
            Assert.Equal(payload, got);

            var list = await p.ListAsync("omnicam/");
            Assert.Single(list);
            Assert.Equal("omnicam/jobs/t.txt", list[0].Key);
            Assert.Equal(payload.Length, list[0].Size);

            await p.DeleteAsync("omnicam/jobs/t.txt");
            Assert.False(await p.ExistsAsync("omnicam/jobs/t.txt"));
            await Assert.ThrowsAsync<CloudStorageException>(() => p.DownloadAsync("omnicam/jobs/t.txt"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Local_provider_rejects_path_escape()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-cloud-" + Guid.NewGuid().ToString("N"));
        var p = new LocalStorageProvider(dir);
        await Assert.ThrowsAsync<CloudStorageException>(() => p.UploadAsync("../evil.txt", new MemoryStream([1])));
        await Assert.ThrowsAsync<CloudStorageException>(() => p.DownloadAsync("..\\evil.txt"));
        await Assert.ThrowsAsync<CloudStorageException>(() => p.ExistsAsync("/abs.txt"));
    }

    [Fact]
    public async Task CloudSync_disabled_never_throws()
    {
        var sync = new CloudSync(new CloudStorageConfig { Enabled = false });
        var up = await sync.UploadTextAsync("temp", "x.txt", "hi");
        Assert.False(up.Ok);
        Assert.Equal("cloud disabled", up.Error);

        var dl = await sync.DownloadTextAsync("temp", "x.txt");
        Assert.False(dl.Ok);

        var (ok, _, err) = await sync.ListAsync("temp");
        Assert.False(ok);
        Assert.Equal("cloud disabled", err);
    }

    [Fact]
    public async Task CloudSync_local_provider_end_to_end()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cabinetnc-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "proj.db");
        await File.WriteAllTextAsync(file, "sqlite-bytes-here");
        var rootDir = Path.Combine(dir, "cloudroot");
        try
        {
            var sync = new CloudSync(new CloudStorageConfig
            {
                Enabled = true,
                Provider = "local",
                LocalRoot = rootDir,
            });
            var st = sync.GetStatus();
            Assert.True(st.Ready);

            var up = await sync.UploadFileAsync("omnicam/projects", "proj.db", file);
            Assert.True(up.Ok, up.Error);
            Assert.Equal("omnicam/projects/proj.db", up.Key);
            Assert.True(File.Exists(Path.Combine(rootDir, "omnicam", "projects", "proj.db")));

            var dl = await sync.DownloadTextAsync("omnicam/projects", "proj.db");
            Assert.True(dl.Ok, dl.Error);
            Assert.Equal("sqlite-bytes-here", dl.Value);

            var del = await sync.DeleteAsync("omnicam/projects", "proj.db");
            Assert.True(del.Ok, del.Error);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Real bucket — skipped unless env opts in. Same flow as check-cloud.mjs.</summary>
    [Fact]
    public async Task Gcs_round_trip_when_configured()
    {
        var cfg = Env();
        if (!cfg.Enabled || cfg.Provider != "gcp" || string.IsNullOrEmpty(cfg.GcpBucket))
            return; // offline CI — the JS harness (scripts/check-cloud.mjs) covers the live path

        var p = new GcpStorageProvider(cfg);
        var key = $"temp/dotnet-storage-test-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.txt";
        var text = $"omnicam .net gcs test {DateTime.UtcNow:o}";

        await p.UploadAsync(key, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain");
        Assert.True(await p.ExistsAsync(key));

        var got = Encoding.UTF8.GetString(await p.DownloadAsync(key));
        Assert.Equal(text, got);

        var list = await p.ListAsync("temp/");
        Assert.Contains(list, i => i.Key == key);

        await p.DeleteAsync(key);
        Assert.False(await p.ExistsAsync(key));
    }
}
