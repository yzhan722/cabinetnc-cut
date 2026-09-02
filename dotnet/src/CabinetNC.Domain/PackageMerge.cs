namespace CabinetNC.Domain;

using CabinetNC.Domain.Materials;
using CabinetNC.Domain.Parts;

/// <summary>Stamp and merge multiple .cnjob packages into one nest list.</summary>
public static class PackageMerge
{
    public static string SuggestId(CutPackage pkg, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(pkg.JobId))
            return Sanitize(pkg.JobId);
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
        return "package";
    }

    public static string SuggestLabel(CutPackage pkg, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(pkg.JobId))
            return pkg.JobId.Trim();
        if (!string.IsNullOrWhiteSpace(sourcePath))
            return Path.GetFileNameWithoutExtension(sourcePath) ?? "方案";
        return "方案";
    }

    public static CutPackage Stamp(CutPackage pkg, string packageId, string? label = null)
    {
        var id = Sanitize(packageId);
        var name = string.IsNullOrWhiteSpace(label) ? id : label.Trim();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var panels = pkg.Panels.Select(p => StampPanel(p, id, name, used, prefixId: false)).ToList();
        return pkg.WithPanels(panels);
    }

    public static CutPackage Merge(CutPackage into, CutPackage incoming, string incomingId, string incomingLabel)
    {
        var usedPkg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in into.Panels)
        {
            if (!string.IsNullOrWhiteSpace(p.Identity?.PackageId))
                usedPkg.Add(p.Identity.PackageId);
        }

        var pkgId = Sanitize(incomingId);
        var n = 2;
        var unique = pkgId;
        while (usedPkg.Contains(unique))
        {
            unique = pkgId + "-" + n;
            n++;
        }

        var usedPanel = new HashSet<string>(into.Panels.Select(p => p.PanelId), StringComparer.OrdinalIgnoreCase);
        var stamped = incoming.Panels
            .Select(p => StampPanel(p, unique, incomingLabel, usedPanel, prefixId: true))
            .ToList();

        var sheets = MergeSheets(into.Sheets, incoming.Sheets);
        var jobId = string.IsNullOrWhiteSpace(into.JobId) ? incoming.JobId : into.JobId;
        if (usedPkg.Count + 1 > 1 && !string.IsNullOrWhiteSpace(into.JobId) && into.JobId != incoming.JobId)
            jobId = into.JobId;
        return into.WithPanels(into.Panels.Concat(stamped).ToList())
            .WithSheets(sheets)
            .WithJobId(jobId);
    }

    /// <summary>Drop every panel stamped as <paramref name="packageKey"/> (left-rail Package node).</summary>
    public static CutPackage Remove(CutPackage pkg, string packageKey)
    {
        if (string.IsNullOrWhiteSpace(packageKey)) return pkg;
        var key = packageKey.Trim();
        var remain = pkg.Panels.Where(p => !MatchesKey(p, key)).ToList();
        if (remain.Count == pkg.Panels.Count) return pkg;
        var sheets = PruneSheets(pkg.Sheets, remain);
        var jobId = pkg.JobId;
        if (string.Equals(jobId, key, StringComparison.OrdinalIgnoreCase))
            jobId = remain.Select(p => p.DisplayPackage).FirstOrDefault();
        return pkg.WithPanels(remain).WithSheets(sheets).WithJobId(jobId);
    }

    public static bool MatchesKey(Panel panel, string? packageKey)
    {
        if (string.IsNullOrWhiteSpace(packageKey)) return false;
        var key = packageKey.Trim();
        return string.Equals(panel.DisplayPackage, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(panel.Identity?.PackageId, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(panel.Identity?.PackageLabel, key, StringComparison.OrdinalIgnoreCase);
    }

    static Panel StampPanel(
        Panel panel,
        string packageId,
        string packageLabel,
        HashSet<string> usedPanelIds,
        bool prefixId)
    {
        var rawId = panel.PanelId;
        var nextId = prefixId && !rawId.StartsWith(packageId + "/", StringComparison.OrdinalIgnoreCase)
            ? packageId + "/" + rawId
            : rawId;
        var unique = nextId;
        var i = 2;
        while (!usedPanelIds.Add(unique))
        {
            unique = nextId + "_" + i;
            i++;
        }

        var prev = panel.Identity;
        var identity = new WorkpieceIdentity
        {
            PackageId = packageId,
            PackageLabel = packageLabel,
            ProjectId = prev?.ProjectId,
            ModuleId = prev?.ModuleId,
            WorkpieceId = prev?.WorkpieceId ?? rawId,
            Role = prev?.Role,
            SourcePath = prev?.SourcePath,
            SourceFormat = prev?.SourceFormat,
        };
        return panel.WithTree(unique, identity);
    }

    static IReadOnlyList<SheetStock> MergeSheets(
        IReadOnlyList<SheetStock> into,
        IReadOnlyList<SheetStock> incoming)
    {
        var list = into.ToList();
        foreach (var s in incoming)
        {
            var dup = list.Any(e =>
                string.Equals(e.Material ?? "", s.Material ?? "", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(e.ThicknessMm - s.ThicknessMm) < 0.05);
            if (dup) continue;
            var id = s.SheetId;
            var n = 2;
            while (list.Any(e => e.SheetId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                id = s.SheetId + "-" + n;
                n++;
            }
            list.Add(new SheetStock
            {
                SheetId = id,
                Material = s.Material,
                ThicknessMm = s.ThicknessMm,
                WidthMm = s.WidthMm,
                LengthMm = s.LengthMm,
                MarginMm = s.MarginMm,
                KerfMm = s.KerfMm,
                PartClearanceMm = s.PartClearanceMm,
                DefectRegions = s.DefectRegions,
            });
        }
        return list;
    }

    /// <summary>Keep sheet kinds still used by remaining panels; if none match, leave stock as-is.</summary>
    static IReadOnlyList<SheetStock> PruneSheets(IReadOnlyList<SheetStock> sheets, IReadOnlyList<Panel> panels)
    {
        if (sheets.Count == 0 || panels.Count == 0) return sheets;
        var kept = sheets.Where(s =>
            panels.Any(p =>
                string.Equals(p.Material ?? "", s.Material ?? "", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(p.ThicknessMm - s.ThicknessMm) < 0.05)).ToList();
        return kept.Count == 0 ? sheets : kept;
    }

    /// <summary>Same shop part across packages: name + material + thickness + outline size.</summary>
    public static string StockKey(Panel panel)
    {
        var pts = panel.Outline.Points;
        var w = 0d;
        var h = 0d;
        if (pts.Count >= 2)
        {
            w = pts.Max(p => p.X) - pts.Min(p => p.X);
            h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        }
        var part = panel.DisplayPartName;
        if (string.IsNullOrWhiteSpace(part))
            part = panel.DisplayTitle;
        return string.Join('|',
            part.Trim().ToLowerInvariant(),
            (panel.Material ?? "").Trim().ToLowerInvariant(),
            Math.Round(panel.ThicknessMm, 1).ToString("0.0"),
            Math.Round(w, 1).ToString("0.0"),
            Math.Round(h, 1).ToString("0.0"),
            pts.Count.ToString(),
            panel.Features.Count.ToString());
    }

    public static IReadOnlyList<IReadOnlyList<Panel>> GroupIdenticalStock(IEnumerable<Panel> panels) =>
        panels
            .GroupBy(StockKey, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<Panel>)g.ToList())
            .ToList();

    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "package";
        var chars = raw.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                chars[i] = '_';
        }
        var s = new string(chars).Trim();
        return s.Length == 0 ? "package" : s;
    }
}
