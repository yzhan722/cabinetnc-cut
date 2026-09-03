namespace CabinetNC.Desktop.Core;

/// <summary>
/// Which opener a dropped / double-clicked / command-line file goes to. Kinds match
/// <c>RecentFile.Kind</c>: "package" (job), "project" (SQLite), "anc" (machine program → reverse).
/// </summary>
public static class FileRouting
{
    public static string? KindFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".anc" or ".nc" => "anc",
            ".db" => "project",
            ".cnjob" or ".zip" or ".json" => "package",
            _ => null,
        };
    }

    /// <summary>First argument that is an existing file we know how to open; ignores switches.</summary>
    public static string? FirstOpenable(IEnumerable<string> args, Func<string, bool> fileExists)
    {
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith('-') || a.StartsWith('/')) continue;
            var trimmed = a.Trim('"');
            if (KindFor(trimmed) is not null && fileExists(trimmed))
                return trimmed;
        }
        return null;
    }
}
