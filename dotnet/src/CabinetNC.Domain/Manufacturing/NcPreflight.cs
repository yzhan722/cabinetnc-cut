namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Nesting;

public sealed record PreflightIssue(string Level, string Code, string Message);

public sealed class PreflightReport
{
    public required bool Ok { get; init; }
    public IReadOnlyList<PreflightIssue> Issues { get; init; } = [];
}

/// <summary>Port of src/nc_preflight.js — shop gate before NC/DXF export.</summary>
public static class NcPreflight
{
    public static PreflightReport Check(
        IReadOnlyList<CutOp> ops,
        MachineProfile profile,
        double sheetWidthMm,
        double sheetLengthMm,
        IReadOnlyDictionary<string, Parts.Panel>? panelsById = null,
        FaceRegistration? registration = null)
    {
        var issues = new List<PreflightIssue>();
        var placed = ops.Where(o => o.Placed).ToList();
        if (placed.Count == 0)
        {
            issues.Add(new("error", "no_ops", "无已排工序 — 先密排并启用轮廓/钻孔/开槽"));
            return new PreflightReport { Ok = false, Issues = issues };
        }

        if (sheetWidthMm > 0 && sheetLengthMm > 0)
        {
            var oob = 0;
            foreach (var op in placed)
            {
                if (op.Op == GuillotineCutPlanner.OpKind)
                    continue;
                foreach (var (x, y) in PointsOf(op))
                {
                    if (x < -0.5 || y < -0.5 || x > sheetWidthMm + 0.5 || y > sheetLengthMm + 0.5)
                        oob++;
                }
            }
            if (oob > 0)
                issues.Add(new("error", "out_of_sheet", $"{oob} 个刀位点超出板材 {sheetWidthMm:0.###}×{sheetLengthMm:0.###}"));
        }

        if (profile.FeedXyMmMin <= 0)
            issues.Add(new("error", "bad_feed", "XY 进给无效"));
        if (profile.SpindleRpm <= 0)
            issues.Add(new("warn", "no_spindle", "主轴转速未设置"));
        if (profile.ToolDiameterMm <= 0)
            issues.Add(new("warn", "no_tool", "刀径为 0"));

        var missingTools = ToolBinder.MissingToolIds(placed);
        if (missingTools.Count > 0)
        {
            issues.Add(new("error", "missing_tool_id",
                $"缺少刀具绑定 ToolId ×{missingTools.Count}: " + string.Join(", ", missingTools.Take(8))));
        }

        issues.AddRange(PocketSafetyIssues(placed));
        issues.AddRange(GrooveClearIssues(placed));

        if (panelsById is not null)
            issues.AddRange(CamSafety.DepthIssues(placed, panelsById));

        // Dual-face: B ops require registration (Day 11). Default session has no strategy → block B.
        issues.AddRange(DoubleSideGate.CheckBackSideOps(placed, registration));

        var ok = issues.All(i => i.Level != "error");
        return new PreflightReport { Ok = ok, Issues = issues };
    }

    public static string Format(PreflightReport report)
    {
        if (report.Issues.Count == 0) return "预检通过";
        return string.Join("\n", report.Issues.Select(i => (i.Level == "error" ? "✗ " : "! ") + i.Message));
    }

    /// <summary>Pocket must have explicit depth and a tool-fit clear path — never silent skip.</summary>
    public static IReadOnlyList<PreflightIssue> PocketSafetyIssues(IEnumerable<CutOp> ops)
    {
        var issues = new List<PreflightIssue>();
        foreach (var op in ops.Where(o => o.Placed && o.Enabled && o.Op == "pocket"))
        {
            if (op.DepthMm is null or <= 0)
            {
                issues.Add(new("error", "pocket_depth_missing",
                    $"pocket/{op.PanelId}/{op.FeatureId ?? "-"}: 缺少明确 DepthMm，禁止默认切穿板厚"));
            }
            if (op.PocketTooSmallForTool
                || (op.PathSegments is null or { Count: 0 }
                    && op.FinishLoop is null or { Count: < 3 }))
            {
                issues.Add(new("error", "pocket_too_small_for_tool",
                    $"pocket/{op.PanelId}/{op.FeatureId ?? "-"}: 型腔过小，刀具无法加工（禁止静默跳过）"));
            }
        }
        return issues;
    }

    /// <summary>Wide groove must have a 回转 clear path — never a silent centreline.</summary>
    public static IReadOnlyList<PreflightIssue> GrooveClearIssues(IEnumerable<CutOp> ops)
    {
        var issues = new List<PreflightIssue>();
        foreach (var op in ops.Where(o => o.Placed && o.Enabled && o.Op == "groove"))
        {
            var toolDia = ClearanceToolPick.DiameterOf(op.ToolId ?? TroyRecipe.TongueToolId);
            var width = op.WidthMm ?? 0;
            if (op.PocketTooSmallForTool)
            {
                issues.Add(new("error", "groove_too_narrow_for_tool",
                    $"groove/{op.PanelId}/{op.FeatureId ?? "-"}: 槽宽 {width:0.###} 小于刀具 Ø{toolDia:0.###}，无法清满"));
                continue;
            }
            if (CamStrategy.NeedsGrooveClear(width, toolDia)
                && (op.PathSegments is null or { Count: 0 })
                && (op.FinishLoop is null or { Count: < 3 }))
            {
                issues.Add(new("error", "groove_width_not_cleared",
                    $"groove/{op.PanelId}/{op.FeatureId ?? "-"}: 槽宽 {width:0.###} > 刀径，未生成回转清底"));
            }
        }
        return issues;
    }

    static IEnumerable<(double X, double Y)> PointsOf(CutOp op)
    {
        if (op.Op == "drill" && op.SheetX is double sx && op.SheetY is double sy)
        {
            yield return (sx, sy);
            yield break;
        }
        if (op.Path is { Count: > 0 } path)
        {
            foreach (var p in path) yield return p;
        }
        if (op.PathSegments is { Count: > 0 })
        {
            foreach (var seg in op.PathSegments)
            {
                foreach (var p in seg) yield return p;
            }
        }
        if (op.FinishLoop is { Count: > 0 } finish)
        {
            foreach (var p in finish) yield return p;
        }
    }
}
