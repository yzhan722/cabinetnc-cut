namespace CabinetNC.Verify;

using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;

/// <summary>
/// Whitelisted repairs: a failed report becomes stricter process choices for the
/// features named in it. Never touches tolerances, never loosens anything, and says
/// "nothing left" (null) for codes that mean an algorithm or CAD defect.
/// </summary>
public sealed class RepairPlanner : IRepairPlanner
{
    /// <summary>Tight stepover safe for every shop cutter ≥ Ø3.</summary>
    public const double TightStepoverMm = 2.0;

    public CamOverrides? Propose(
        VerifyReport report, CamOverrides current, IReadOnlyDictionary<string, ToolDefinition> tools)
    {
        var next = current;
        var smallest = SmallestMill(tools);

        foreach (var issue in report.Issues.Where(i => i.IsError && i.FeatureId is not null))
        {
            var key = (issue.PanelId, issue.FeatureId!);
            var cur = next.Get(key.PanelId, key.Item2) ?? new FeatureCamOverride();
            FeatureCamOverride? proposed = issue.Code switch
            {
                VerifyCodes.PocketFloorUncut or VerifyCodes.GrooveFloorUncut => FloorLeft(cur, smallest),
                VerifyCodes.FeatureNotCut => NotReached(cur, smallest),
                VerifyCodes.GrooveWidthMismatch => WidthOff(cur, issue, smallest),
                VerifyCodes.BlindOvercut or VerifyCodes.ThroughOvercut => SmallerTool(cur, smallest),
                // depth, drill position/diameter, unknown tool: no process knob fixes these.
                _ => null,
            };
            if (proposed is null || proposed == cur) continue;
            next = next.With(key.PanelId, key.Item2, _ => proposed);
        }

        return next.SameAs(current) ? null : next;
    }

    /// <summary>Residual floor: first tighten the stepover and add a wall pass, then a smaller cutter.</summary>
    static FeatureCamOverride? FloorLeft(FeatureCamOverride cur, string? smallest)
    {
        if (cur.StepoverMm is null || !cur.ForceFinishLoop)
            return cur with { StepoverMm = Math.Min(cur.StepoverMm ?? double.MaxValue, TightStepoverMm), ForceFinishLoop = true };
        return SmallerTool(cur, smallest);
    }

    /// <summary>Nothing reached the feature: usually the cutter did not fit — try the smallest one.</summary>
    static FeatureCamOverride? NotReached(FeatureCamOverride cur, string? smallest) =>
        SmallerTool(cur, smallest);

    static FeatureCamOverride? WidthOff(FeatureCamOverride cur, VerifyIssue issue, string? smallest)
    {
        var narrow = issue.MeasuredMm is double m && issue.ExpectedMm is double e && m < e;
        if (narrow)
        {
            if (!cur.ForceGrooveClear || !cur.ForceFinishLoop)
                return cur with { ForceGrooveClear = true, ForceFinishLoop = true };
            return SmallerTool(cur, smallest);
        }
        return SmallerTool(cur, smallest);
    }

    static FeatureCamOverride? SmallerTool(FeatureCamOverride cur, string? smallest)
    {
        if (smallest is null) return null;
        if (string.Equals(cur.ToolId, smallest, StringComparison.OrdinalIgnoreCase)) return null;
        return cur with { ToolId = smallest };
    }

    /// <summary>Smallest non-drill cutter in the catalog (T1 Ø6.35 on the Troy shop).</summary>
    static string? SmallestMill(IReadOnlyDictionary<string, ToolDefinition> tools) =>
        tools.Values
            .Where(t => t.DiameterMm > 0 && !t.Role.Contains("drill", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.DiameterMm)
            .Select(t => t.ToolId)
            .FirstOrDefault();
}
