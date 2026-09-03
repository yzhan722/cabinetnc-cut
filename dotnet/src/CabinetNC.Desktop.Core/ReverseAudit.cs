using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Desktop.Core;

public sealed record ReverseAuditRow(string PanelId, string Size, string Features);

/// <summary>
/// "Did every stroke of the machine program end up on a recovered panel?" — the question the
/// operator must answer before re-cutting from a reverse-engineered .anc.
/// </summary>
public sealed record ReverseAuditSummary(
    int Strokes,
    int Contours,
    int Drills,
    int Grooves,
    int Pockets,
    int RemnantCuts,
    int Panels,
    int Windows,
    int OrphanContours,
    int OrphanFeatures,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ReverseAuditRow> Rows)
{
    public bool AllAccounted => Problems.Count == 0;
}

public static class ReverseAudit
{
    public static ReverseAuditSummary Summarize(NcReverseResult r)
    {
        var contours = r.Ops.Count(o => o.Op == "contour");
        var drills = r.Ops.Count(o => o.Op == "drill");
        var grooves = r.Ops.Count(o => o.Op == "groove");
        var pockets = r.Ops.Count(o => o.Op == "pocket");
        var remnant = r.Ops.Count(o => o.Op == "remnant");
        var panels = r.Panels.Count;
        var windows = r.Panels.Sum(p => p.Features.Count(IsWindow));
        var holesOwned = r.Panels.Sum(p => p.Features.Count(IsHole));
        var groovesOwned = r.Panels.Sum(p => p.Features.Count(IsGroove));
        var pocketsOwned = r.Panels.Sum(p => p.Features.Count(IsPocket));

        var orphanContours = Math.Max(0, contours - panels - windows);
        var orphanFeatures = Math.Max(0, drills - holesOwned) + Math.Max(0, grooves - groovesOwned) + Math.Max(0, pockets - pocketsOwned);

        var problems = new List<string>();
        if (orphanContours > 0) problems.Add($"{orphanContours} 个闭合外形没有归为板或开窗");
        if (orphanFeatures > 0) problems.Add($"{orphanFeatures} 个孔/槽/口袋不在任何板内");
        problems.AddRange(r.Warnings);

        var rows = r.Panels.Select(Row).ToList();
        return new ReverseAuditSummary(
            r.Strokes.Count, contours, drills, grooves, pockets, remnant, panels, windows,
            orphanContours, orphanFeatures, problems, rows);
    }

    public static string MetaLine(ReverseAuditSummary s, double safeZ, double thickness) =>
        $"程序 {s.Strokes} 段运动 · 安全高 {safeZ:0} · 板厚 {thickness:0.#}\n" +
        $"闭合外形 {s.Contours} → 板 {s.Panels} + 开窗 {s.Windows} · 孔 {s.Drills} · 槽 {s.Grooves} · 口袋 {s.Pockets}" +
        (s.RemnantCuts > 0 ? $" · 余料切线 {s.RemnantCuts}" : "");

    public static string WarningLine(ReverseAuditSummary s) =>
        string.Join(" · ", s.Problems) + " — 上机前对照原程序核对，缺失的特征不会出现在重切件上";

    static ReverseAuditRow Row(Panel p)
    {
        var pts = p.Outline.Points;
        var w = pts.Count > 0 ? pts.Max(q => q.X) - pts.Min(q => q.X) : 0;
        var h = pts.Count > 0 ? pts.Max(q => q.Y) - pts.Min(q => q.Y) : 0;
        var parts = new List<string>();
        var holes = p.Features.Count(IsHole);
        var grv = p.Features.Count(IsGroove);
        var win = p.Features.Count(IsWindow);
        var pk = p.Features.Count(IsPocket);
        if (holes > 0) parts.Add($"孔 {holes}");
        if (grv > 0) parts.Add($"槽 {grv}");
        if (win > 0) parts.Add($"开窗 {win}");
        if (pk > 0) parts.Add($"口袋 {pk}");
        return new ReverseAuditRow(p.PanelId, $"{w:0.#} × {h:0.#}", parts.Count == 0 ? "无特征" : string.Join(" · ", parts));
    }

    static bool IsHole(PanelFeature f) => f.Kind.Contains("hole", StringComparison.OrdinalIgnoreCase);
    static bool IsGroove(PanelFeature f) => f.Kind.Contains("groove", StringComparison.OrdinalIgnoreCase);
    static bool IsWindow(PanelFeature f) => f.Kind == "cutout";
    static bool IsPocket(PanelFeature f) => f.Kind == "pocket";
}
