namespace CabinetNC.Infrastructure.Cloud;

/// <summary>
/// Cloud object-key conventions — identical rules to Cab Lab
/// (the-cab-lab/cloud/paths.js) and the JS prototype (src/cloud/paths.js).
///
/// Bucket layout (flat prefixes, refined by project/job id in a later phase):
///   cablab/   Cab Lab private data (jobs, generator io, bom)
///   omnicam/  OmniCam private data (jobs, nest, cam, nc, dxf)
///   shared/   data passed between the two (e.g. cnjob manufacturing snapshots)
///   temp/     transient compute / transfer files
/// </summary>
public static class CloudPaths
{
    public static readonly IReadOnlyList<string> Roots = ["cablab", "omnicam", "shared", "temp"];

    static readonly char[] InvalidChars = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    /// <summary>Join and normalise a key: forward slashes, no empty segments.</summary>
    public static string JoinKey(params string?[] parts) =>
        string.Join('/', parts
            .SelectMany(p => (p ?? "").Split('/'))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0));

    public static bool IsSafeRelKey(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        if (rel.StartsWith('/') || rel.StartsWith('\\')) return false;
        if (rel.IndexOfAny(InvalidChars) >= 0) return false;
        if (rel.Contains("..") || rel.Contains('\0')) return false;
        if (rel.EndsWith('/')) return false;
        return true;
    }

    /// <summary>
    /// Build an object key under one of the shared roots. `root` may be
    /// "omnicam/nc" (nested under a root) — the first segment must be a known
    /// root so neither app can write outside its lane by accident.
    /// </summary>
    public static string CloudKey(string root, params string?[] rel)
    {
        var key = JoinKey([root, .. rel]);
        if (!IsSafeRelKey(key))
            throw new CloudStorageException($"unsafe cloud key: {root}/{string.Join("/", rel)}");
        var first = key.Split('/')[0];
        if (!Roots.Contains(first))
            throw new CloudStorageException($"cloud key must start with {string.Join("|", Roots)}: {key}");
        if (key == first)
            throw new CloudStorageException($"cloud key needs a name under {first}: {key}");
        return key;
    }
}
