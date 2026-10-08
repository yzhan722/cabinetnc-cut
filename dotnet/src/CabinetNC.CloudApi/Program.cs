using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var app = builder.Build();

var bucketName = Environment.GetEnvironmentVariable("BUCKET") ?? "omnicam_jobs";
var token = Environment.GetEnvironmentVariable("OMNICAM_CLOUD_TOKEN") ?? "";
var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
};
var storage = await StorageClient.CreateAsync();
var signer = UrlSigner.FromCredential(await GoogleCredential.GetApplicationDefaultAsync());

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }
    if (string.IsNullOrEmpty(token))
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("cloud token is not configured");
        return;
    }
    // Cloud Run treats Authorization as a Google identity token and rejects anything else.
    var header = ctx.Request.Headers["X-OmniCam-Token"].ToString();
    if (!header.Equals(token, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.MapGet("/v1/jobs", async () =>
{
    var jobs = new List<JobListItem>();
    await foreach (var obj in storage.ListObjectsAsync(bucketName, "jobs/"))
    {
        if (!obj.Name.EndsWith("/meta.json", StringComparison.Ordinal)) continue;
        try
        {
            using var ms = new MemoryStream();
            await storage.DownloadObjectAsync(bucketName, obj.Name, ms);
            var meta = JsonSerializer.Deserialize<JobMeta>(ms.ToArray(), jsonOpts);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Id)) continue;
            jobs.Add(new JobListItem(meta.Id, meta.Name, meta.UploadedAt, meta.SheetCount));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Skipped {Object}", obj.Name);
        }
    }
    return jobs
        .OrderByDescending(j => j.UploadedAt, StringComparer.Ordinal)
        .Take(200)
        .ToList();
});

app.MapPost("/v1/jobs", async (BeginRequest body) =>
{
    var name = (body.Name ?? "").Trim();
    if (name.Length == 0 || name.Length > 200)
        return Results.BadRequest(new { error = "name is required" });
    var files = (body.Files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal).ToList();
    if (files.Count == 0 || files.Count > 400)
        return Results.BadRequest(new { error = "files must list the project and the syntec pack" });
    if (files.Any(f => !CloudObjectPath.Allowed(f)))
        return Results.BadRequest(new { error = "a file path is not allowed" });

    var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")[..8];
    var meta = new JobMeta
    {
        Id = id,
        Name = name,
        UploadedAt = DateTimeOffset.UtcNow.ToString("O"),
        SheetCount = Math.Max(0, body.SheetCount),
        MachineId = string.IsNullOrWhiteSpace(body.MachineId) ? "syntec_e4_1330" : body.MachineId.Trim(),
    };
    var metaJson = JsonSerializer.Serialize(meta, jsonOpts);
    var paths = files.Append("meta.json").Distinct(StringComparer.Ordinal).ToList();
    var uploads = new SignedFile[paths.Count];
    using var gate = new SemaphoreSlim(12);
    await Task.WhenAll(paths.Select(async (path, index) =>
    {
        await gate.WaitAsync();
        try
        {
            var url = await signer.SignAsync(
                bucketName,
                CloudObjectPath.ObjectName(id, path),
                TimeSpan.FromMinutes(20),
                HttpMethod.Put);
            uploads[index] = new SignedFile(path, url);
        }
        finally
        {
            gate.Release();
        }
    }));
    return Results.Ok(new BeginResponse(id, metaJson, uploads));
});

app.MapGet("/v1/jobs/{id}/project", async (string id) =>
{
    if (!CloudObjectPath.ValidJobId(id))
        return Results.BadRequest(new { error = "unknown job" });
    var objectName = CloudObjectPath.ObjectName(id, "project.db");
    try
    {
        await storage.GetObjectAsync(bucketName, objectName);
    }
    catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound(new { error = "project file is missing" });
    }
    var url = await signer.SignAsync(bucketName, objectName, TimeSpan.FromMinutes(10), HttpMethod.Get);
    return Results.Ok(new ProjectFileResponse(url));
});

app.MapDelete("/v1/jobs/{id}", async (string id) =>
{
    if (!CloudObjectPath.ValidJobId(id))
        return Results.BadRequest(new { error = "unknown job" });
    var prefix = "jobs/" + id + "/";
    var names = new List<string>();
    await foreach (var obj in storage.ListObjectsAsync(bucketName, prefix))
    {
        if (obj.Name.StartsWith(prefix, StringComparison.Ordinal))
            names.Add(obj.Name);
    }
    if (names.Count == 0)
        return Results.NotFound();
    foreach (var name in names)
        await storage.DeleteObjectAsync(bucketName, name);
    return Results.NoContent();
});

app.Run();

static class CloudObjectPath
{
    public static bool Allowed(string path)
    {
        if (path is "project.db") return true;
        if (path.Contains('\\') || path.Contains("..", StringComparison.Ordinal))
            return false;
        if (!path.StartsWith("syntec/", StringComparison.Ordinal)) return false;
        var rest = path["syntec/".Length..];
        if (rest.Length == 0 || rest.StartsWith('/') || rest.Contains("//", StringComparison.Ordinal))
            return false;
        if (rest == "1.xml") return true;
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash != rest.LastIndexOf('/')) return false;
        var folder = rest[..slash];
        var file = rest[(slash + 1)..];
        if (file.Length == 0) return false;
        foreach (var c in file)
        {
            if (c < ' ' || c is '/' or '\\') return false;
        }
        return folder switch
        {
            "cnc" => file.EndsWith(".nc", StringComparison.OrdinalIgnoreCase),
            "label" => file.EndsWith(".cyc", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static string ObjectName(string id, string path) => "jobs/" + id + "/" + path;

    public static bool ValidJobId(string id)
    {
        if (id.Length is < 8 or > 80) return false;
        foreach (var c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-' or 'T'))
                return false;
        }
        return true;
    }
}

sealed class BeginRequest
{
    public string? Name { get; set; }
    public int SheetCount { get; set; }
    public string? MachineId { get; set; }
    public List<string>? Files { get; set; }
}

sealed record BeginResponse(string Id, string MetaJson, IReadOnlyList<SignedFile> Uploads);

sealed record ProjectFileResponse(string Url);

sealed record SignedFile(string Path, string Url);

sealed record JobListItem(string Id, string Name, string UploadedAt, int SheetCount);

sealed class JobMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string UploadedAt { get; set; } = "";
    public int SheetCount { get; set; }
    public string MachineId { get; set; } = "";
}
