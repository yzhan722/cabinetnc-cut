using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Non-secret intranet preferences, kept next to library.json.</summary>
public sealed record CloudSettings(ComputeMode Mode, string? ServerUrl, string? Tenant, string? Email)
{
    public static CloudSettings Default => new(ComputeMode.Local, null, null, null);
}

public sealed class CloudSettingsStore(string directory)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = System.IO.Path.Combine(directory, "cloud.json");

    public CloudSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
                return CloudSettings.Default;
            return JsonSerializer.Deserialize<CloudSettings>(File.ReadAllText(Path), Json) ?? CloudSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return CloudSettings.Default;
        }
    }

    public void Save(CloudSettings settings)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
    }
}

/// <summary>
/// A random GUID minted on first use and kept forever for this install. Deliberately not derived from
/// hardware identifiers: it must not leak anything about the machine and must change on reinstall.
/// </summary>
public sealed class DeviceIdentityStore(string directory)
{
    public string Path { get; } = System.IO.Path.Combine(directory, "device-id");

    public Guid GetOrCreate()
    {
        try
        {
            if (File.Exists(Path) && Guid.TryParseExact(File.ReadAllText(Path).Trim(), "D", out var existing) && existing != Guid.Empty)
                return existing;
        }
        catch (IOException)
        {
            // fall through and mint a new one
        }

        var created = Guid.NewGuid();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path, created.ToString("D"));
        return created;
    }
}

/// <summary>What survives a restart: only the refresh token and the identity it belongs to.</summary>
public sealed record StoredRefreshToken(string RefreshToken, string DeviceId, string Tenant, string Email, DateTimeOffset ExpiresAtUtc);

public interface ITokenStore
{
    StoredRefreshToken? Load();
    void Save(StoredRefreshToken token);
    void Clear();
}

/// <summary>DPAPI (current user) protected file. Only this Windows account on this machine can read it.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTokenStore(string directory) : ITokenStore
{
    // Bound to the purpose so a blob copied from another CabinetNC file cannot be replayed here.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CabinetNC.Cloud.RefreshToken.v1");

    public string Path { get; } = System.IO.Path.Combine(directory, "cloud.token");

    public StoredRefreshToken? Load()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredRefreshToken>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(StoredRefreshToken token)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(token);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path, protectedBytes);
        CryptographicOperations.ZeroMemory(plain);
    }

    public void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }
}
