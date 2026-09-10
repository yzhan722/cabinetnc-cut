namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

/// <summary>
/// Area-clearance tool from the feature's short side. Hinge cups always T2 Ø10.
/// </summary>
public static class ClearanceToolPick
{
    public const double LargeMinShortMm = 17;
    /// <summary>Only holes strictly smaller than this are drill cycles. Ø5 and up mill as clearance.</summary>
    public const double DrillMaxExclusiveMm = 5;
    /// <summary>Blind circular cups at or above this Ø (Blum 26 / 35) force T2.</summary>
    public const double HingeCupMinDiameterMm = 26;
    public const int CupCircleSegments = 48;
    public const string LargeToolId = "T2";
    public const string SmallToolId = "T1";

    public static double NormalizeLargeMinShortMm(double mm) =>
        Math.Clamp(mm, 4, 80);

    public static double NormalizeDrillMaxExclusiveMm(double mm) =>
        Math.Clamp(mm, 1, 20);

    public static bool IsHingeFeature(PanelFeature f)
    {
        var blob = $"{f.Purpose} {f.Kind} {f.FeatureId}";
        if (blob.Contains("hinge", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("铰链", StringComparison.Ordinal)
            || blob.Contains("铰杯", StringComparison.Ordinal))
            return true;
        // Fusion exports cups as bore/holeVertical with no purpose — size is the shop tag.
        if (!PanelEdit.IsHole(f) || f.Through)
            return false;
        return f.DiameterMm is >= HingeCupMinDiameterMm;
    }

    public static bool IsDrillHole(PanelFeature f, double maxExclusiveMm = DrillMaxExclusiveMm) =>
        PanelEdit.IsHole(f)
        && f.DiameterMm is > 0
        && f.DiameterMm.Value < NormalizeDrillMaxExclusiveMm(maxExclusiveMm);

    /// <summary>Closed mill outline for a hinge cup (CAD ring, else circle from centre + Ø).</summary>
    public static IReadOnlyList<(double X, double Y)>? CupOutline(PanelFeature f)
    {
        if (f.Path is { Count: >= 3 } path)
            return path.Select(p => (p.X, p.Y)).ToList();
        if (f.Profile is { Count: >= 3 } profile)
            return profile.Select(p => (p.X, p.Y)).ToList();
        if (f.DiameterMm is not > 1e-9)
            return null;
        var r = f.DiameterMm.Value / 2;
        var pts = new List<(double X, double Y)>(CupCircleSegments);
        for (var i = 0; i < CupCircleSegments; i++)
        {
            var a = 2 * Math.PI * i / CupCircleSegments;
            pts.Add((f.X + r * Math.Cos(a), f.Y + r * Math.Sin(a)));
        }
        return pts;
    }

    public static double ShortSideMm(
        PanelFeature f,
        IReadOnlyList<IReadOnlyList<Point2>>? islandHoles = null)
    {
        if (PanelEdit.IsPocket(f) || (f.Kind.Contains("pocket", StringComparison.OrdinalIgnoreCase)
                && (f.Path is { Count: >= 3 } || f.Profile is { Count: >= 3 })))
        {
            var ring = f.Path is { Count: >= 3 } ? f.Path : f.Profile;
            if (ring is { Count: >= 3 })
            {
                var band = RebateBandMm(ring, islandHoles ?? f.Holes);
                if (band > 1e-9)
                    return band;
                return ShortSpan(ring);
            }
        }

        if (f.WidthMm is > 1e-9)
            return f.WidthMm.Value;
        var inferred = GrooveGeometry.InferWidthMm(f.Path, f.Profile);
        if (inferred > 1e-9)
            return inferred;
        if (f.DiameterMm is > 1e-9)
            return f.DiameterMm.Value;
        if (f.Path is { Count: >= 3 })
            return ShortSpan(f.Path);
        return 0;
    }

    public static string Pick(
        PanelFeature f,
        double largeMinShortMm = LargeMinShortMm,
        double smallToolDiaMm = TroyRecipe.TongueDiameterMm,
        IReadOnlyList<IReadOnlyList<Point2>>? islandHoles = null)
    {
        largeMinShortMm = NormalizeLargeMinShortMm(largeMinShortMm);
        if (IsHingeFeature(f))
            return LargeToolId;
        var shortMm = ShortSideMm(f, islandHoles);
        if (shortMm >= largeMinShortMm)
            return LargeToolId;
        if (shortMm > smallToolDiaMm)
            return SmallToolId;
        return SmallToolId;
    }

    public static double DiameterOf(string toolId) =>
        ToolCatalog.DefaultMap().TryGetValue(toolId, out var def) && def.DiameterMm > 0
            ? def.DiameterMm
            : TroyRecipe.WorkDiameterMm;

    static double RebateBandMm(IReadOnlyList<Point2> outer, IReadOnlyList<IReadOnlyList<Point2>>? holes)
    {
        if (holes is not { Count: > 0 }) return 0;
        var hole = holes.FirstOrDefault(h => h.Count >= 3);
        if (hole is null) return 0;
        var dw = LongSpan(outer) - LongSpan(hole);
        var dh = ShortSpan(outer) - ShortSpan(hole);
        var band = Math.Min(dw, dh) / 2;
        return band > 0.5 ? band : 0;
    }

    static double ShortSpan(IReadOnlyList<Point2> ring)
    {
        var minX = ring.Min(p => p.X);
        var maxX = ring.Max(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxY = ring.Max(p => p.Y);
        return Math.Min(maxX - minX, maxY - minY);
    }

    static double LongSpan(IReadOnlyList<Point2> ring)
    {
        var minX = ring.Min(p => p.X);
        var maxX = ring.Max(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxY = ring.Max(p => p.Y);
        return Math.Max(maxX - minX, maxY - minY);
    }
}
