namespace CabinetNC.Domain.Manufacturing;

using System.Globalization;
using System.Text;
using CabinetNC.Domain;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

/// <summary>Port of src/job_sheet.js — printable shop HTML.</summary>
public static class JobSheetBuilder
{
    public static string BuildHtml(
        CutPackage pkg,
        MachineProfile profile,
        IReadOnlyList<NestPlacement>? placements,
        IReadOnlySet<string>? locked,
        string? preflightText,
        double? utilizationPct,
        int unplacedCount)
    {
        var name = pkg.JobId ?? "job";
        var placeBy = (placements ?? []).ToDictionary(p => p.PanelId, p => p);
        var mats = string.Join(", ", pkg.Panels.Select(p => p.Material ?? "—").Distinct());
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine($"<title>Job · {Esc(name)}</title>");
        sb.AppendLine("<style>body{font:13px/1.4 system-ui,sans-serif;margin:24px}table{border-collapse:collapse;width:100%;max-width:720px}");
        sb.AppendLine("th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}th{background:#f4f4f4}@media print{button{display:none}}</style></head><body>");
        sb.AppendLine("<button onclick=\"print()\">Print</button>");
        sb.AppendLine($"<h1>OmniCam · {Esc(name)}</h1>");
        sb.AppendLine("<div style=\"color:#444;margin-bottom:16px;white-space:pre-wrap\">");
        sb.AppendLine($"Format: {Esc(pkg.SchemaName)} v{pkg.Version}");
        sb.AppendLine($"Machine: {Esc(profile.Name.Replace("新代", "Syntec", StringComparison.Ordinal))} · Ø{profile.ToolDiameterMm}");
        sb.AppendLine($"Parts: {pkg.Panels.Count} · placed: {placeBy.Count} · unplaced: {unplacedCount}");
        if (utilizationPct is double u) sb.AppendLine($"Utilization: {u.ToString("0.0", CultureInfo.InvariantCulture)}%");
        sb.AppendLine($"Material: {Esc(mats)}");
        if (!string.IsNullOrWhiteSpace(preflightText))
            sb.AppendLine("Preflight:\n" + Esc(preflightText));
        sb.AppendLine("</div>");
        sb.AppendLine("<table><thead><tr><th>Part</th><th>Size</th><th>Sheet</th><th>Lock</th></tr></thead><tbody>");
        foreach (var p in pkg.Panels)
        {
            var (w, h) = BBox(p);
            placeBy.TryGetValue(p.PanelId, out var pl);
            var sheet = pl is null ? "—" : $"S{pl.SheetIndex + 1}";
            var lockMark = locked is not null && locked.Contains(p.PanelId) ? "L" : "";
            sb.AppendLine($"<tr><td>{Esc(p.PanelId)}</td><td>{w:0}×{h:0}</td><td>{sheet}</td><td>{lockMark}</td></tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    static (double W, double H) BBox(Panel p)
    {
        var pts = p.Outline.Points;
        if (pts.Count < 2) return (0, 0);
        return (pts.Max(x => x.X) - pts.Min(x => x.X), pts.Max(x => x.Y) - pts.Min(x => x.Y));
    }

    static string Esc(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
