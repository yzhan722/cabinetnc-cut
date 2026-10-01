namespace CabinetNC.Domain.Manufacturing.Verification;

/// <summary>
/// The only knobs the repair loop may turn on one feature. Each is a *stricter* or
/// *safer* process choice — nothing here can widen a tolerance or skip a check.
/// </summary>
public sealed record FeatureCamOverride
{
    /// <summary>Tighter stepover for area clearance (mm).</summary>
    public double? StepoverMm { get; init; }
    /// <summary>Area-clear a groove even when its width is within the tool-width band.</summary>
    public bool ForceGrooveClear { get; init; }
    /// <summary>Use this tool instead of the auto pick (only ever a smaller cutter).</summary>
    public string? ToolId { get; init; }
    /// <summary>Always run a CAD-size wall pass after clearing.</summary>
    public bool ForceFinishLoop { get; init; }

    public bool IsEmpty => StepoverMm is null && !ForceGrooveClear && ToolId is null && !ForceFinishLoop;

    public override string ToString()
    {
        var bits = new List<string>();
        if (StepoverMm is double s) bits.Add($"stepover={s:0.##}");
        if (ForceGrooveClear) bits.Add("grooveClear");
        if (ToolId is not null) bits.Add($"tool={ToolId}");
        if (ForceFinishLoop) bits.Add("finishLoop");
        return string.Join(",", bits);
    }
}

/// <summary>Per-feature process overrides keyed by (panelId, featureId). Immutable.</summary>
public sealed class CamOverrides
{
    readonly Dictionary<(string Panel, string Feature), FeatureCamOverride> _map;

    CamOverrides(Dictionary<(string, string), FeatureCamOverride> map) => _map = map;

    public static CamOverrides Empty { get; } = new([]);

    public int Count => _map.Count;
    public bool IsEmpty => _map.Count == 0;

    public FeatureCamOverride? Get(string panelId, string? featureId) =>
        featureId is null ? null : _map.GetValueOrDefault((panelId, featureId));

    public IEnumerable<((string PanelId, string FeatureId) Key, FeatureCamOverride Value)> All =>
        _map.Select(kv => (kv.Key, kv.Value));

    public CamOverrides With(string panelId, string featureId, Func<FeatureCamOverride, FeatureCamOverride> change)
    {
        var next = new Dictionary<(string, string), FeatureCamOverride>(_map);
        var cur = next.GetValueOrDefault((panelId, featureId)) ?? new FeatureCamOverride();
        next[(panelId, featureId)] = change(cur);
        return new CamOverrides(next);
    }

    public bool SameAs(CamOverrides other)
    {
        if (other._map.Count != _map.Count) return false;
        foreach (var (k, v) in _map)
        {
            if (!other._map.TryGetValue(k, out var o) || o != v) return false;
        }
        return true;
    }

    public string Describe() =>
        _map.Count == 0
            ? "(none)"
            : string.Join("; ", _map.OrderBy(kv => kv.Key.Panel, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Feature, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key.Panel}/{kv.Key.Feature}:{kv.Value}"));
}

/// <summary>
/// Turns a failed <see cref="VerifyReport"/> into the next set of overrides to try, or
/// null when the whitelist has nothing left — that is an algorithm defect, not a process choice.
/// </summary>
public interface IRepairPlanner
{
    CamOverrides? Propose(VerifyReport report, CamOverrides current, IReadOnlyDictionary<string, ToolDefinition> tools);
}

/// <summary>One round of the plan → emit → verify loop.</summary>
public sealed record RepairRound(int Round, CamOverrides Overrides, VerifyReport Report)
{
    public bool Ok => Report.Ok;
}
