using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Nesting;

/// <summary>
/// Model-side geometry of parts-in-part slots: what the Desktop needs to validate, draw and drag
/// children inside host cut-outs. The packing algorithm itself lives in CabinetNC.Domain.Compute.
/// </summary>
public static class PartsInPartGeometry
{
    /// <summary>A void narrower than this cannot hold anything worth nesting.</summary>
    public const double MinVoidMm = 20;

    public static HashSet<(string A, string B)> IgnoreCollisionPairs(IEnumerable<PartInPartSlot> slots)
    {
        var set = new HashSet<(string, string)>();
        foreach (var s in slots)
        {
            if (!s.Enabled) continue;
            set.Add((s.HostPanelId, s.ChildPanelId));
            set.Add((s.ChildPanelId, s.HostPanelId));
        }
        return set;
    }

    /// <summary>Usable opening of a host cutout in sheet coordinates (inset by clearance).</summary>
    public static bool TryUsableVoid(
        Panel host,
        double hostOx,
        double hostOy,
        double hostRotDeg,
        string? featureId,
        double clearanceMm,
        out double minX,
        out double minY,
        out double width,
        out double height)
    {
        minX = minY = width = height = 0;
        var hostBounds = NestTransform.BoundsOf(host);
        var inset = Math.Max(0, clearanceMm);
        (double MinX, double MinY, double W, double H)? best = null;
        foreach (var f in host.Features)
        {
            if (!PanelEdit.IsCutout(f)) continue;
            if (featureId is not null
                && !string.Equals(f.FeatureId, featureId, StringComparison.Ordinal))
                continue;
            var ring = f.Path ?? f.Profile;
            if (ring is not { Count: >= 3 }) continue;

            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            foreach (var pt in ring)
            {
                var (sx, sy) = NestTransform.ToSheet(
                    pt.X, pt.Y, hostBounds,
                    hostOx, hostOy, hostRotDeg);
                x0 = Math.Min(x0, sx);
                y0 = Math.Min(y0, sy);
                x1 = Math.Max(x1, sx);
                y1 = Math.Max(y1, sy);
            }

            var ux0 = x0 + inset;
            var uy0 = y0 + inset;
            var uw = (x1 - inset) - ux0;
            var uh = (y1 - inset) - uy0;
            if (uw < MinVoidMm || uh < MinVoidMm) continue;
            if (best is null || uw * uh > best.Value.W * best.Value.H)
                best = (ux0, uy0, uw, uh);
            if (featureId is not null) break;
        }
        if (best is null) return false;
        minX = best.Value.MinX;
        minY = best.Value.MinY;
        width = best.Value.W;
        height = best.Value.H;
        return true;
    }
}
