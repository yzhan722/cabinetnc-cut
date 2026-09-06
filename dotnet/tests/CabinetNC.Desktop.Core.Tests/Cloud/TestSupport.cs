using CabinetNC.Desktop.Core.Cloud;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

public sealed class InMemoryTokenStore : ITokenStore
{
    public StoredRefreshToken? Stored { get; private set; }
    public int SaveCount { get; private set; }
    public int ClearCount { get; private set; }

    public StoredRefreshToken? Load() => Stored;

    public void Save(StoredRefreshToken token)
    {
        Stored = token;
        SaveCount++;
    }

    public void Clear()
    {
        Stored = null;
        ClearCount++;
    }
}

/// <summary>Deterministic clock; the client only reads <see cref="GetUtcNow"/>.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A throwaway directory for the file-backed stores.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cabinetnc-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Fact that only runs on Windows (DPAPI).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows DPAPI only.";
    }
}

public static class ClientFixtures
{
    public static CloudClientOptions Options(FakeCloudApiHandler? handler = null) => new()
    {
        BaseAddress = new Uri("https://cabinetnc.test"),
        Tenant = "shop",
        JobTimeout = TimeSpan.FromMinutes(10),
        NetworkGrace = TimeSpan.FromSeconds(60),
    };

    public static CloudApiClient Client(FakeCloudApiHandler handler, ITokenStore tokens, TempDirectory dir, TestClock clock) =>
        new(Options(), tokens, new DeviceIdentityStore(dir.Path), clock, handler);
}
