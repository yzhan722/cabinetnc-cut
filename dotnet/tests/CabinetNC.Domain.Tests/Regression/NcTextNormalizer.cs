namespace CabinetNC.Domain.Tests.Regression;

/// <summary>Stable NC compare: drop N-words and decor comments, keep motion and tool identity.</summary>
public static class NcTextNormalizer
{
    public static string Normalize(string? nc)
    {
        var text = (nc ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var kept = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = StripNPrefix(raw.TrimEnd());
            if (line.Length == 0) continue;
            if (IsDecorComment(line)) continue;
            kept.Add(line);
        }

        return kept.Count == 0 ? "\n" : string.Join('\n', kept) + "\n";
    }

    static string StripNPrefix(string line)
    {
        if (line.Length < 2 || (line[0] != 'N' && line[0] != 'n'))
            return line;
        if (!char.IsDigit(line[1]))
            return line;
        var i = 1;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;
        return line[i..];
    }

    static bool IsDecorComment(string line)
    {
        var t = line.TrimStart();
        if (t.Length == 0 || t[0] != '(')
            return false;
        var lower = t.ToLowerInvariant();
        return lower.StartsWith("(cabinetnc-cut", StringComparison.Ordinal)
               || lower.StartsWith("(wcs:", StringComparison.Ordinal)
               || lower.StartsWith("(cam safety:", StringComparison.Ordinal)
               || lower.StartsWith("(origin:", StringComparison.Ordinal);
    }
}
