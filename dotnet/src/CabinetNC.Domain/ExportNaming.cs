namespace CabinetNC.Domain;

using System.Globalization;

/// <summary>Shop file stems: ordinal + thickness + color + kind + project.</summary>
public static class ExportNaming
{
    public static string FileStem(string? raw, string empty = "project")
    {
        if (string.IsNullOrWhiteSpace(raw)) return empty;
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
        return stem.Length == 0 ? empty : stem;
    }

    public static string ThicknessToken(double thicknessMm)
    {
        var v = Math.Abs(thicknessMm - Math.Round(thicknessMm)) < 0.05
            ? Math.Round(thicknessMm).ToString("0", CultureInfo.InvariantCulture)
            : thicknessMm.ToString("0.#", CultureInfo.InvariantCulture);
        return v + "mm";
    }

    public static string AncFileName(
        int kindOrdinal,
        double thicknessMm,
        string? color,
        string? kind,
        string? project) =>
        $"{Math.Max(1, kindOrdinal):00}_{ThicknessToken(thicknessMm)}_{FileStem(color, "Unassigned")}_{FileStem(kind, "Board")}_{FileStem(project)}.anc";
}
