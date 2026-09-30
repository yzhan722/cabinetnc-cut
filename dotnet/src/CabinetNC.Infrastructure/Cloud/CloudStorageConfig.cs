namespace CabinetNC.Infrastructure.Cloud;

/// <summary>
/// Cloud storage configuration — same variable names and JSON file keys as
/// Cab Lab (the-cab-lab/cloud/config.js) and the JS prototype (src/cloud/).
///
/// Sources, lowest precedence first:
///   1. JSON file at CAB_CLOUD_CONFIG (optional local file, never committed)
///   2. environment variables
///
/// Env / JSON keys:
///   CAB_CLOUD_ENABLED      "1" | "true" turns cloud sync on (default off)
///   CAB_CLOUD_PROVIDER     "gcp" (default) | "local" (mirror into a local dir)
///   CAB_CLOUD_ROOT         object-key root for this app (default "omnicam")
///   CAB_GCP_PROJECT_ID     GCP project id
///   CAB_GCP_BUCKET         bucket name (e.g. cab-platform-dev-315764)
///   CAB_GCP_REGION         asia-southeast1 (informational; GCS is global-API)
///   CAB_GCP_CREDENTIALS    service-account key JSON path (else ADC / gcloud)
///   CAB_GCP_ACCESS_TOKEN   explicit bearer token (short-lived; dev/tests)
///   CAB_CLOUD_LOCAL_ROOT   root dir for the "local" provider
/// </summary>
public sealed class CloudStorageConfig
{
    public bool Enabled { get; init; }
    public string Provider { get; init; } = "gcp";
    public string Root { get; init; } = "omnicam";
    public string? GcpProjectId { get; init; }
    public string? GcpBucket { get; init; }
    public string GcpRegion { get; init; } = "asia-southeast1";
    public string? GcpCredentialsFile { get; init; }
    public string? GcpAccessToken { get; init; }
    public string LocalRoot { get; init; } = Path.Combine(Path.GetTempPath(), "cab-cloud-local");

    public static CloudStorageConfig FromEnvironment(IDictionary<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value);

        var file = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (env.TryGetValue("CAB_CLOUD_CONFIG", out var configPath) && !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
                    File.ReadAllText(configPath)) ?? [];
                foreach (var kv in json)
                    file[kv.Key] = kv.Value.ValueKind == System.Text.Json.JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString();
            }
            catch { /* unreadable config file: fall back to env only */ }
        }

        string? Pick(string envName, string fileKey) =>
            env.TryGetValue(envName, out var v) && !string.IsNullOrEmpty(v) ? v
                : file.TryGetValue(fileKey, out var f) ? f
                : null;

        var enabledRaw = Pick("CAB_CLOUD_ENABLED", "enabled") ?? "";
        return new CloudStorageConfig
        {
            Enabled = enabledRaw is "1" or "true" or "yes" or "on",
            Provider = (Pick("CAB_CLOUD_PROVIDER", "provider") ?? "gcp").ToLowerInvariant(),
            Root = (Pick("CAB_CLOUD_ROOT", "root") ?? "omnicam").Trim('/'),
            GcpProjectId = Pick("CAB_GCP_PROJECT_ID", "gcpProjectId"),
            GcpBucket = Pick("CAB_GCP_BUCKET", "gcpBucket"),
            GcpRegion = Pick("CAB_GCP_REGION", "gcpRegion") ?? "asia-southeast1",
            GcpCredentialsFile = Pick("CAB_GCP_CREDENTIALS", "gcpCredentialsFile")
                ?? (env.TryGetValue("GOOGLE_APPLICATION_CREDENTIALS", out var gac) ? gac : null),
            GcpAccessToken = Pick("CAB_GCP_ACCESS_TOKEN", "gcpAccessToken"),
            LocalRoot = Pick("CAB_CLOUD_LOCAL_ROOT", "localRoot") ?? Path.Combine(Path.GetTempPath(), "cab-cloud-local"),
        };
    }
}
