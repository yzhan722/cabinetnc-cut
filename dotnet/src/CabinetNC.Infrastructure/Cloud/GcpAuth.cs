using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CabinetNC.Infrastructure.Cloud;

/// <summary>
/// OAuth2 bearer-token resolution for the GCS JSON API. Same precedence as the
/// JS layer (src/cloud/gcs.js, the-cab-lab/cloud/gcs.js):
///   1. cfg.GcpAccessToken            explicit bearer (dev / tests)
///   2. cfg.GcpCredentialsFile        service-account JSON → signed JWT grant
///   3. gcloud application-default creds file (~ authorized_user refresh grant
///      or service_account)
///   4. `gcloud auth … print-access-token` subprocess (application-default,
///      then the plain login token — the latter works after `gcloud auth login`)
/// Dev machines normally land on (4); a later phase should issue a service
/// account per worker instead.
/// </summary>
internal static class GcpAuth
{
    const string TokenUri = "https://oauth2.googleapis.com/token";
    const string Scope = "https://www.googleapis.com/auth/devstorage.read_write";

    static (string Token, DateTimeOffset ExpiresAt)? _cache;

    public static async Task<string> ResolveAccessTokenAsync(
        CloudStorageConfig cfg, HttpClient http, bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cache is { } c && DateTimeOffset.UtcNow < c.ExpiresAt - TimeSpan.FromMinutes(1))
            return c.Token;
        if (!string.IsNullOrEmpty(cfg.GcpAccessToken))
            return cfg.GcpAccessToken;

        string? token;
        if (!string.IsNullOrEmpty(cfg.GcpCredentialsFile) && File.Exists(cfg.GcpCredentialsFile))
        {
            token = await ServiceAccountTokenAsync(cfg.GcpCredentialsFile, http, ct);
        }
        else
        {
            var adc = AdcFile();
            if (adc is not null)
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(adc, ct));
                var el = doc.RootElement;
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "authorized_user" && el.TryGetProperty("refresh_token", out var rt))
                {
                    token = await TokenRequestAsync(http, new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = el.GetProperty("client_id").GetString()!,
                        ["client_secret"] = el.GetProperty("client_secret").GetString()!,
                        ["refresh_token"] = rt.GetString()!,
                    }, ct);
                }
                else if (type == "service_account")
                {
                    token = await ServiceAccountTokenAsync(adc, http, ct);
                }
                else
                {
                    token = null;
                }
            }
            else
            {
                token = null;
            }

            token ??= GcloudToken("application-default") ?? GcloudToken();
            if (token is null)
                throw new CloudStorageException(
                    "no GCP credentials — set CAB_GCP_CREDENTIALS / CAB_GCP_ACCESS_TOKEN, or run: gcloud auth login",
                    provider: "gcp");
        }

        _cache = (token, DateTimeOffset.UtcNow.AddMinutes(40));
        return token;
    }

    static async Task<string> ServiceAccountTokenAsync(string keyFile, HttpClient http, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(keyFile, ct));
        var el = doc.RootElement;
        var clientEmail = el.GetProperty("client_email").GetString()!;
        var privateKey = el.GetProperty("private_key").GetString()!;
        var tokenUri = el.TryGetProperty("token_uri", out var tu) ? tu.GetString()! : TokenUri;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail,
            scope = Scope,
            aud = tokenUri,
            iat = now,
            exp = now + 3600,
        }));
        var unsigned = $"{header}.{payload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKey);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return await TokenRequestAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = $"{unsigned}.{Base64Url(sig)}",
        }, ct, tokenUri);
    }

    static async Task<string> TokenRequestAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct, string uri = TokenUri)
    {
        using var res = await http.PostAsync(uri, new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!res.IsSuccessStatusCode || !doc.RootElement.TryGetProperty("access_token", out var at))
        {
            var desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : res.ReasonPhrase;
            throw new CloudStorageException($"oauth token failed {(int)res.StatusCode}: {desc}", (int)res.StatusCode, "gcp");
        }
        return at.GetString()!;
    }

    static string? AdcFile()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var dir = !string.IsNullOrEmpty(appData)
            ? Path.Combine(appData, "gcloud")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud");
        var file = Path.Combine(dir, "application_default_credentials.json");
        return File.Exists(file) ? file : null;
    }

    static string? GcloudToken(params string[] extraArgs)
    {
        // gcloud is gcloud.cmd on Windows — Process.Start won't resolve .cmd
        // without going through cmd.exe. All args are fixed literals.
        var args = $"auth {string.Join(" ", extraArgs)} print-access-token";
        var commands = new List<string> { $"gcloud {args}" };
        if (OperatingSystem.IsWindows())
        {
            // winget install lands here and does not touch PATH
            var sdk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Cloud SDK", "google-cloud-sdk", "bin", "gcloud.cmd");
            if (File.Exists(sdk)) commands.Add($"\"{sdk}\" {args}");
        }

        foreach (var cmd in commands)
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/d /c {cmd}")
                : new ProcessStartInfo("gcloud", args);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            try
            {
                using var p = Process.Start(psi);
                if (p is null) continue;
                if (!p.WaitForExit(15_000)) { try { p.Kill(); } catch { } continue; }
                var outText = p.StandardOutput.ReadToEnd().Trim();
                if (p.ExitCode == 0 && outText.Length > 0) return outText;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
    }

    static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Test seam.</summary>
    internal static void ResetCache() => _cache = null;
}
