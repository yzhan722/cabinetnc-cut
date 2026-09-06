using System.Text;
using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

public class DeviceIdentityStoreTests
{
    [Fact]
    public void Creates_a_random_guid_once_and_returns_it_forever_after()
    {
        using var dir = new TempDirectory();

        var first = new DeviceIdentityStore(dir.Path).GetOrCreate();
        var second = new DeviceIdentityStore(dir.Path).GetOrCreate();

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
        Assert.Equal(first.ToString("D"), File.ReadAllText(Path.Combine(dir.Path, "device-id")).Trim());
    }

    [Fact]
    public void Two_installs_get_different_ids()
    {
        using var a = new TempDirectory();
        using var b = new TempDirectory();

        Assert.NotEqual(new DeviceIdentityStore(a.Path).GetOrCreate(), new DeviceIdentityStore(b.Path).GetOrCreate());
    }

    [Fact]
    public void Corrupt_file_is_replaced_instead_of_crashing()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "device-id"), "not a guid");

        var id = new DeviceIdentityStore(dir.Path).GetOrCreate();

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, new DeviceIdentityStore(dir.Path).GetOrCreate());
    }
}

public class CloudSettingsStoreTests
{
    [Fact]
    public void Missing_file_means_local_mode_defaults()
    {
        using var dir = new TempDirectory();

        var settings = new CloudSettingsStore(dir.Path).Load();

        Assert.Equal(ComputeMode.Local, settings.Mode);
        Assert.Null(settings.ServerUrl);
    }

    [Fact]
    public void Round_trips_and_never_contains_secrets()
    {
        using var dir = new TempDirectory();
        var store = new CloudSettingsStore(dir.Path);

        store.Save(new CloudSettings(ComputeMode.Intranet, "https://cabinetnc.shop.local", "shop", "op@example.internal"));
        var loaded = store.Load();

        Assert.Equal(ComputeMode.Intranet, loaded.Mode);
        Assert.Equal("https://cabinetnc.shop.local", loaded.ServerUrl);
        Assert.Equal("shop", loaded.Tenant);
        Assert.Equal("op@example.internal", loaded.Email);
        Assert.DoesNotContain("password", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", File.ReadAllText(store.Path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "cloud.json"), "{ this is not json");

        Assert.Equal(ComputeMode.Local, new CloudSettingsStore(dir.Path).Load().Mode);
    }
}

public class WindowsTokenStoreTests
{
    static readonly StoredRefreshToken Sample = new("refresh-secret-value", "device-1", "shop", "op@example.internal", new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));

    [WindowsFact]
    public void Round_trips_through_dpapi_without_plaintext_on_disk()
    {
        using var dir = new TempDirectory();
        var store = new WindowsTokenStore(dir.Path);

        store.Save(Sample);
        var loaded = store.Load();

        Assert.Equal(Sample, loaded);
        var raw = File.ReadAllBytes(store.Path);
        ByteAssert.DoesNotContain(Encoding.UTF8.GetBytes(Sample.RefreshToken), raw);
        ByteAssert.DoesNotContain(Encoding.UTF8.GetBytes(Sample.Email), raw);
    }

    [WindowsFact]
    public void Clear_removes_the_file_and_load_returns_null()
    {
        using var dir = new TempDirectory();
        var store = new WindowsTokenStore(dir.Path);
        store.Save(Sample);

        store.Clear();

        Assert.Null(store.Load());
        Assert.False(File.Exists(store.Path));
    }

    [WindowsFact]
    public void Tampered_blob_is_treated_as_absent()
    {
        using var dir = new TempDirectory();
        var store = new WindowsTokenStore(dir.Path);
        store.Save(Sample);
        var raw = File.ReadAllBytes(store.Path);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(store.Path, raw);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Missing_file_is_absent()
    {
        using var dir = new TempDirectory();
        Assert.Null(new WindowsTokenStore(dir.Path).Load());
    }
}

file static class ByteAssert
{
    public static void DoesNotContain(byte[] needle, byte[] haystack) =>
        Assert.True(haystack.AsSpan().IndexOf(needle) < 0, "plaintext found in protected file");
}
