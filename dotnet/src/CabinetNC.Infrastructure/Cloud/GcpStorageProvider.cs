using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CabinetNC.Infrastructure.Cloud;

/// <summary>Google Cloud Storage via the JSON API (see GcpAuth for the token story).</summary>
public sealed class GcpStorageProvider : ICloudStorage
{
    const string Api = "https://storage.googleapis.com";

    readonly CloudStorageConfig _cfg;
    readonly HttpClient _http;

    public GcpStorageProvider(CloudStorageConfig cfg, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(cfg.GcpBucket))
            throw new CloudStorageException("gcp provider needs CAB_GCP_BUCKET", provider: "gcp");
        _cfg = cfg;
        _http = http ?? new HttpClient();
    }

    public string Bucket => _cfg.GcpBucket!;

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct, bool retry401 = true)
    {
        var token = await GcpAuth.ResolveAccessTokenAsync(_cfg, _http, ct: ct);
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null) req.Content = content;
        var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized && retry401 && string.IsNullOrEmpty(_cfg.GcpAccessToken))
        {
            res.Dispose();
            await GcpAuth.ResolveAccessTokenAsync(_cfg, _http, refresh: true, ct);
            return await SendAsync(method, url, content, ct, retry401: false);
        }
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new CloudStorageException(
                $"gcs {method} {url} → {(int)res.StatusCode} {text[..Math.Min(300, text.Length)]}",
                (int)res.StatusCode, "gcp");
        }
        return res;
    }

    static string Enc(string key) => Uri.EscapeDataString(key);

    public async Task<CloudObjectInfo> UploadAsync(string key, Stream data, string? contentType = null, CancellationToken ct = default)
    {
        var size = data.CanSeek ? data.Length : 0;
        var url = $"{Api}/upload/storage/v1/b/{Bucket}/o?uploadType=media&name={Enc(key)}";
        using var content = new StreamContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        using var res = await SendAsync(HttpMethod.Post, url, content, ct);
        return new CloudObjectInfo { Key = key, Size = size, Updated = DateTimeOffset.UtcNow };
    }

    public async Task<byte[]> DownloadAsync(string key, CancellationToken ct = default)
    {
        var url = $"{Api}/storage/v1/b/{Bucket}/o/{Enc(key)}?alt=media";
        using var res = await SendAsync(HttpMethod.Get, url, null, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        var url = $"{Api}/storage/v1/b/{Bucket}/o/{Enc(key)}?fields=name";
        try
        {
            using var res = await SendAsync(HttpMethod.Get, url, null, ct);
            return true;
        }
        catch (CloudStorageException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var url = $"{Api}/storage/v1/b/{Bucket}/o/{Enc(key)}";
        try
        {
            using var res = await SendAsync(HttpMethod.Delete, url, null, ct);
        }
        catch (CloudStorageException ex) when (ex.Status == 404)
        {
            // missing is not an error
        }
    }

    public async Task<IReadOnlyList<CloudObjectInfo>> ListAsync(string prefix = "", CancellationToken ct = default)
    {
        var list = new List<CloudObjectInfo>();
        var pageToken = "";
        do
        {
            var url = $"{Api}/storage/v1/b/{Bucket}/o?prefix={Uri.EscapeDataString(prefix)}&fields=items(name,size,updated),nextPageToken"
                + (pageToken.Length > 0 ? $"&pageToken={pageToken}" : "");
            using var res = await SendAsync(HttpMethod.Get, url, null, ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var it in items.EnumerateArray())
                {
                    list.Add(new CloudObjectInfo
                    {
                        Key = it.GetProperty("name").GetString()!,
                        Size = it.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var sz) ? sz : 0,
                        Updated = it.TryGetProperty("updated", out var u) && DateTimeOffset.TryParse(u.GetString(), out var dt) ? dt : null,
                    });
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() ?? "" : "";
        } while (pageToken.Length > 0);
        return list;
    }
}
