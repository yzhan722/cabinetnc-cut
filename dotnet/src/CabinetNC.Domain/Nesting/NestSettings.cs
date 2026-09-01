namespace CabinetNC.Domain.Nesting;

/// <summary>
/// Single source of truth for nest parameters (Day 5).
/// ASSUMPTION defaults: border 15, spacing 12, grain lock when grain set → 0/180 only.
/// </summary>
public sealed class NestSettings
{
    public double MarginMm { get; init; } = 15;
    public double ClearanceMm { get; init; } = 12;
    public bool AllowRotation { get; init; } = true;
    /// <summary>Global allowed nest rotations. Empty = unconstrained (subject to grain lock).</summary>
    public IReadOnlyList<int> AllowedRotations { get; init; } = [];
    public double RotationStepDeg { get; init; } = 90;
    /// <summary>When true and no sheet grain is set, panels with grain may only use 0/180.</summary>
    public bool GrainLock { get; init; } = true;
    /// <summary>Shop sheet veneer axis for this stock kind. None = no sheet constraint.</summary>
    public SheetGrainKind SheetGrain { get; init; } = SheetGrainKind.None;
    public bool MirrorPermission { get; init; }
    public bool PreferLockedPlacements { get; init; } = true;

    public IReadOnlyList<string> ValidateConsistency()
    {
        var issues = new List<string>();
        if (MarginMm < 0) issues.Add("margin_negative");
        if (ClearanceMm < 0) issues.Add("clearance_negative");
        if (RotationStepDeg <= 0) issues.Add("rotation_step_invalid");
        if (GrainLock && AllowedRotations.Count > 0)
        {
            var bad = AllowedRotations.Where(r =>
            {
                var n = ((r % 360) + 360) % 360;
                return n is not (0 or 180);
            }).ToList();
            if (bad.Count > 0)
                issues.Add("grain_lock_conflicts_allowed_rotations");
        }
        return issues;
    }

    /// <summary>Whether a panel may use 90° nest rotation under these settings + panel policy.</summary>
    public bool PanelMayRotate90(Parts.Panel panel)
    {
        if (!AllowRotation) return false;
        var aligned = GrainAlign.AlignedRotations(
            panel.GrainDirection ?? panel.Orientation?.GrainDirection, SheetGrain);
        if (aligned.Count > 0)
            return aligned.Any(r => r is 90 or 270);
        if (GrainLock && GrainAlign.HasPartGrain(panel))
            return false;
        if (AllowedRotations.Count > 0)
            return AllowedRotations.Any(r =>
            {
                var n = ((r % 360) + 360) % 360;
                return n is 90 or 270;
            });
        return panel.MayRotate90;
    }

    /// <summary>Discrete nest angles to try for <paramref name="panel"/>.</summary>
    public IReadOnlyList<double> CandidateRotations(Parts.Panel panel)
    {
        if (!AllowRotation) return [0];
        var aligned = GrainAlign.AlignedRotations(
            panel.GrainDirection ?? panel.Orientation?.GrainDirection, SheetGrain);
        if (aligned.Count > 0) return aligned;
        if (GrainLock && GrainAlign.HasPartGrain(panel))
            return [0, 180];
        if (AllowedRotations.Count > 0)
        {
            return AllowedRotations
                .Select(r => (double)(((r % 360) + 360) % 360))
                .Distinct()
                .OrderBy(r => r)
                .ToList();
        }
        return panel.MayRotate90 ? [0d, 90d, 180d, 270d] : [0d, 180d];
    }
}

/// <summary>Material + thickness grouping key (case-insensitive material, 0.01 mm thickness).</summary>
public readonly record struct NestGroupKey(string Material, double ThicknessMm)
{
    public static NestGroupKey From(string? material, double thicknessMm) =>
        new(
            string.IsNullOrWhiteSpace(material) ? "(unspecified)" : material.Trim(),
            Math.Round(thicknessMm, 2));

    public override string ToString() => $"{Material} · {ThicknessMm:0.##}mm";
}

public sealed class NestUnplacedReason
{
    public required string PanelId { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed class NestGroupReport
{
    public required NestGroupKey Key { get; init; }
    public int PartCount { get; init; }
    public int PlacedCount { get; init; }
    public int SheetCount { get; init; }
    public int LocalSheetStart { get; init; }
    public double UtilizationPct { get; init; }
}
