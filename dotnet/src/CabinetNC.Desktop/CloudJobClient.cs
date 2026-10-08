using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CabinetNC.Desktop;

/// <summary>One file in a workshop upload: the project database or a piece of the Syntec pack.</summary>
public sealed record CloudUploadFile(string RelativePath, byte[] Bytes);

public sealed record CloudUploadBundle(string ProjectName, int SheetCount, string MachineId, IReadOnlyList<CloudUploadFile> Files);

sealed record CloudJobRow(string Id, string Name, string UploadedAt, int SheetCount);

/// <summary>
/// Talks to the workshop Cloud Run service. The storage key stays on that service;
/// this PC only has the service address and a bearer token.
/// </summary>
sealed class CloudJobClient : IDisposable
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    readonly HttpClient _api;
    readonly HttpClient _put;

    CloudJobClient(string baseUrl, string token)
    {
        _api = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        _api.DefaultRequestHeaders.TryAddWithoutValidation("X-OmniCam-Token", token);
        _put = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniCam", "cloud.json");

    public static CloudJobClient? TryLoad(out string? problem)
    {
        string? url = Environment.GetEnvironmentVariable("OMNICAM_CLOUD_URL");
        string? token = Environment.GetEnvironmentVariable("OMNICAM_CLOUD_TOKEN");
        if (File.Exists(ConfigPath))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<CloudConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (string.IsNullOrWhiteSpace(url)) url = cfg?.BaseUrl;
                if (string.IsNullOrWhiteSpace(token)) token = cfg?.Token;
            }
            catch (Exception ex)
            {
                problem = "车间云端配置读不了：" + ex.Message;
                return null;
            }
        }
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            problem = "这台电脑还没有接上车间云端。";
            return null;
        }
        problem = null;
        return new CloudJobClient(url, token);
    }

    public async Task<IReadOnlyList<CloudJobRow>> ListAsync(CancellationToken ct)
    {
        using var response = await _api.GetAsync("v1/jobs", ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(Explain(response.StatusCode, detail));
        }
        var rows = await response.Content.ReadFromJsonAsync<List<CloudJobRow>>(JsonOpts, ct);
        return rows ?? [];
    }

    public async Task DownloadProjectAsync(string id, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在从云端取工程…");
        using var response = await _api.GetAsync("v1/jobs/" + Uri.EscapeDataString(id) + "/project", ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "云端没有这份工程文件。"
                : Explain(response.StatusCode, detail));
        }
        var file = await response.Content.ReadFromJsonAsync<ProjectFileResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("云端没有返回下载地址。");
        using var download = await _put.GetAsync(file.Url, ct);
        if (!download.IsSuccessStatusCode)
        {
            var detail = await download.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(Explain(download.StatusCode, detail));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var stream = File.Create(destPath);
        await download.Content.CopyToAsync(stream, ct);
    }

    public static string LocalPathFor(string id)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniCam", "cloud");
        return Path.Combine(dir, id + ".db");
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var response = await _api.DeleteAsync("v1/jobs/" + Uri.EscapeDataString(id), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(Explain(response.StatusCode, detail));
        }
    }

    public async Task UploadAsync(CloudUploadBundle bundle, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("正在向云端申请上传地址…");
        var begin = await _api.PostAsJsonAsync("v1/jobs", new BeginBody
        {
            Name = bundle.ProjectName,
            SheetCount = bundle.SheetCount,
            MachineId = bundle.MachineId,
            Files = bundle.Files.Select(f => f.RelativePath).ToList(),
        }, JsonOpts, ct);
        if (!begin.IsSuccessStatusCode)
        {
            var detail = await begin.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(Explain(begin.StatusCode, detail));
        }
        var plan = await begin.Content.ReadFromJsonAsync<BeginResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("云端没有返回上传地址。");
        var byPath = bundle.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var data = plan.Uploads.Where(u => !string.Equals(u.Path, "meta.json", StringComparison.Ordinal)).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(data.Select(async slot =>
        {
            ct.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(slot.Path, out var file))
                throw new InvalidOperationException("云端要求的文件不在这包里：" + slot.Path);
            await gate.WaitAsync(ct);
            try
            {
                var n = Interlocked.Increment(ref done);
                progress?.Report($"正在上传 {n}/{data.Count} · {slot.Path}");
                await PutAsync(slot.Url, file.Bytes, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        var meta = plan.Uploads.FirstOrDefault(u => string.Equals(u.Path, "meta.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("云端没有返回清单地址。");
        progress?.Report("正在写入云端清单…");
        await PutAsync(meta.Url, Encoding.UTF8.GetBytes(plan.MetaJson), ct);
    }

    async Task PutAsync(string url, byte[] bytes, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
        using var response = await _put.PutAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(Explain(response.StatusCode, detail));
        }
    }

    static string Explain(System.Net.HttpStatusCode code, string detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? "" : " " + detail.Trim();
        if (text.Length > 300) text = text[..300];
        return $"云端拒绝了这次上传（{(int)code}）。{text}";
    }

    public void Dispose()
    {
        _api.Dispose();
        _put.Dispose();
    }

    sealed class CloudConfig
    {
        public string? BaseUrl { get; set; }
        public string? Token { get; set; }
    }

    sealed class ProjectFileResponse
    {
        public string Url { get; set; } = "";
    }

    sealed class BeginBody
    {
        public string Name { get; set; } = "";
        public int SheetCount { get; set; }
        public string MachineId { get; set; } = "";
        public List<string> Files { get; set; } = [];
    }

    sealed class BeginResponse
    {
        public string Id { get; set; } = "";
        public string MetaJson { get; set; } = "";
        public List<SignedSlot> Uploads { get; set; } = [];
    }

    sealed class SignedSlot
    {
        public string Path { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
