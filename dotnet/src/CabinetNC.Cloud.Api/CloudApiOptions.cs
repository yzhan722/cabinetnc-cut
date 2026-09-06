using System.Text;

namespace CabinetNC.Cloud.Api;

/// <summary>
/// Everything the API needs comes from the environment (design spec §6 / Task 6). Missing or weak
/// values fail startup with a message that names the variable but never echoes its value.
/// </summary>
public sealed class CloudApiOptions
{
    public const string DbConnectionKey = "CABINETNC_DB_CONNECTION";
    public const string JwtSigningKeyKey = "CABINETNC_JWT_SIGNING_KEY";
    public const string BootstrapTenantKey = "CABINETNC_BOOTSTRAP_TENANT";
    public const string BootstrapAdminEmailKey = "CABINETNC_BOOTSTRAP_ADMIN_EMAIL";
    public const string BootstrapAdminPasswordKey = "CABINETNC_BOOTSTRAP_ADMIN_PASSWORD";

    public const int MinSigningKeyBytes = 32;
    public const int MaxSigningKeyBytes = 4096;
    public const int MinBootstrapPasswordLength = 12;
    public const int MaxBootstrapPasswordLength = 1024;

    public required string DbConnection { get; init; }
    public required string JwtSigningKey { get; init; }
    public string JwtIssuer { get; init; } = "cabinetnc-cloud";
    public string JwtAudience { get; init; } = "cabinetnc-desktop";
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);
    public string? BootstrapTenant { get; init; }
    public string? BootstrapAdminEmail { get; init; }
    public string? BootstrapAdminPassword { get; init; }

    /// <summary>
    /// Production secrets deliberately bypass appsettings/command-line configuration. Tests replace
    /// this options singleton directly through WebApplicationFactory.
    /// </summary>
    public static CloudApiOptions FromEnvironment() =>
        FromSource(Environment.GetEnvironmentVariable);

    internal static CloudApiOptions FromSource(Func<string, string?> read)
    {
        var db = read(DbConnectionKey);
        if (string.IsNullOrWhiteSpace(db))
            throw new InvalidOperationException($"{DbConnectionKey} is required.");

        var key = read(JwtSigningKeyKey);
        var keyBytes = string.IsNullOrWhiteSpace(key) ? 0 : Encoding.UTF8.GetByteCount(key);
        if (keyBytes is < MinSigningKeyBytes or > MaxSigningKeyBytes)
            throw new InvalidOperationException(
                $"{JwtSigningKeyKey} is required and must be between " +
                $"{MinSigningKeyBytes} and {MaxSigningKeyBytes} bytes.");

        return new CloudApiOptions
        {
            DbConnection = db,
            JwtSigningKey = key!,
            BootstrapTenant = read(BootstrapTenantKey),
            BootstrapAdminEmail = read(BootstrapAdminEmailKey),
            BootstrapAdminPassword = read(BootstrapAdminPasswordKey),
        };
    }
}
