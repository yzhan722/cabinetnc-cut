namespace CabinetNC.Domain;

/// <summary>Shop file stems: project + material kind + per-kind sheet ordinal.</summary>
public static class ExportNaming
{
    public static string FileStem(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "project";
        var s = PackageMerge.Sanitize(raw.Trim());
        var sb = new System.Text.StringBuilder(s.Length);
        var sep = false;
        foreach (var c in s)
        {
            if (c is '·' or '.' or ',')
            {
                sep = sb.Length > 0;
                continue;
            }
            if (char.IsWhiteSpace(c))
                continue;
            if (sep && c != '_')
                sb.Append('_');
            sep = false;
            sb.Append(c);
        }
        var stem = sb.ToString().Trim('_');
        return stem.Length == 0 ? "project" : stem;
    }

    public static string AncFileName(string project, string kindLabel, int kindOrdinal) =>
        $"{FileStem(project)}_{FileStem(kindLabel)}_{Math.Max(1, kindOrdinal):00}.anc";
}
