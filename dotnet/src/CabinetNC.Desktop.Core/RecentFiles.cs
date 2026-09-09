using CabinetNC.Infrastructure.Library;

namespace CabinetNC.Desktop.Core;

/// <summary>Most-recently-used list rules: newest first, de-duplicated by path, bounded.</summary>
public static class RecentFiles
{
    public const int DefaultMax = 10;

    public static List<RecentFile> Remember(
        IEnumerable<RecentFile> current,
        string fullPath,
        string kind,
        DateTimeOffset now,
        int max = DefaultMax)
    {
        var list = current
            .Where(r => !string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Insert(0, new RecentFile { Path = fullPath, Kind = kind, OpenedAt = now.ToString("o") });
        if (list.Count > max)
            list.RemoveRange(max, list.Count - max);
        return list;
    }

    public static List<RecentFile> Without(IEnumerable<RecentFile> current, string fullPath) =>
        current.Where(r => !string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Kind label shown before the file name (工程 / .anc / 方案).</summary>
    public static string KindLabel(string? kind) => kind switch
    {
        "project" => "工程",
        "anc" => ".anc",
        _ => "方案",
    };

    /// <summary>
    /// WPF treats "_" in Button content / MenuItem headers as an access-key marker and the
    /// automation peers strip it again; file names must escape every underscore.
    /// </summary>
    public static string EscapeAccessKeys(string text) => text.Replace("_", "__", StringComparison.Ordinal);
}
