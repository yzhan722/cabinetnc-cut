namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;
using Clipper2Lib;

/// <summary>
/// Blind area-clear keep-outs. A leftover full-thickness pad always stays.
/// A sibling through profile is ignored when its AABB short side is under
/// <see cref="IgnoreProfileShortMm"/> (mill across). Wider profiles are
/// avoided along that cut. A pocket hole is dropped only when it is the
/// same region as an ignored thin profile.
/// </summary>
public static class PocketClearIslands
{
    public const double IgnoreProfileShortMm = 20;
    const double Scale = 10000;
    const double CoverRatio = 0.5;
    const double MinCoverArea = Scale * Scale * 0.25;

    public static IReadOnlyList<IReadOnlyList<Point2>> Keep(Panel panel, PanelFeature pocket)
    {
        var keep = (pocket.Holes ?? [])
            .Where(ring => ring.Count >= 3)
            .ToList();
        var through = ThroughOpenings(panel, pocket.FeatureId);
        if (through.Count == 0) return keep;

        keep = keep
            .Where(hole => !SameAsIgnoredProfile(hole, through))
            .ToList();

        var outline = OutlineOf(pocket);
        foreach (var opening in through)
        {
            if (ShortSideMm(opening) < IgnoreProfileShortMm) continue;
            if (outline is { Count: >= 3 } && !Overlaps(opening, outline)) continue;
            if (outline is null) continue;
            if (keep.Any(hole => SameRegion(hole, opening) || ContainedIn(opening, hole)))
                continue;
            keep.Add(opening);
        }

        return keep;
    }

    static List<IReadOnlyList<Point2>> ThroughOpenings(Panel panel, string pocketId)
    {
        var rings = new List<IReadOnlyList<Point2>>();
        foreach (var f in panel.Features)
        {
            if (f.FeatureId == pocketId) continue;
            if (!MaterialCorrect.IsThroughOrFullSlot(f, panel.ThicknessMm))
                continue;
            if (OpeningOf(f) is { Count: >= 3 } ring)
                rings.Add(ring);
        }
        return rings;
    }

    static IReadOnlyList<Point2>? OutlineOf(PanelFeature f)
    {
        if (f.Path is { Count: >= 3 } path) return path;
        if (f.Profile is { Count: >= 3 } profile) return profile;
        return null;
    }

    static IReadOnlyList<Point2>? OpeningOf(PanelFeature f)
    {
        if (f.Path is { Count: >= 3 } path)
            return path;
        if (f.Profile is { Count: >= 3 } profile)
            return profile;
        if (PanelEdit.IsGroove(f))
        {
            var slot = GrooveGeometry.DisplayOutline(f);
            if (slot.Count >= 3) return slot;
        }
        if (PanelEdit.IsHole(f) && ClearanceToolPick.CupOutline(f) is { Count: >= 3 } cup)
            return cup.Select(p => new Point2(p.X, p.Y)).ToList();
        return null;
    }

    static bool SameAsIgnoredProfile(
        IReadOnlyList<Point2> hole,
        IReadOnlyList<IReadOnlyList<Point2>> throughs)
    {
        foreach (var opening in throughs)
        {
            if (ShortSideMm(opening) >= IgnoreProfileShortMm) continue;
            if (SameRegion(hole, opening)) return true;
        }
        return false;
    }

    static bool SameRegion(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        var cover = IntersectionArea(a, b);
        if (cover < MinCoverArea) return false;
        var areaA = RingArea(a);
        var areaB = RingArea(b);
        return areaA > 0 && areaB > 0
            && cover >= areaA * CoverRatio
            && cover >= areaB * CoverRatio;
    }

    static bool ContainedIn(IReadOnlyList<Point2> inner, IReadOnlyList<Point2> outer)
    {
        var cover = IntersectionArea(inner, outer);
        var innerArea = RingArea(inner);
        return innerArea > 0 && cover >= innerArea * 0.7;
    }

    static bool Overlaps(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b) =>
        IntersectionArea(a, b) >= MinCoverArea;

    static double IntersectionArea(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        var pa = ToPath64(a);
        var pb = ToPath64(b);
        if (pa.Count < 3 || pb.Count < 3) return 0;
        var hit = Clipper.Intersect(
            new Paths64 { pa },
            new Paths64 { pb },
            FillRule.NonZero);
        var cover = 0d;
        foreach (var p in hit)
            cover += Math.Abs(Clipper.Area(p));
        return cover;
    }

    static double RingArea(IReadOnlyList<Point2> ring)
    {
        var path = ToPath64(ring);
        return path.Count < 3 ? 0 : Math.Abs(Clipper.Area(path));
    }

    static double ShortSideMm(IReadOnlyList<Point2> ring)
    {
        if (ring.Count < 3) return 0;
        var minX = ring.Min(p => p.X);
        var maxX = ring.Max(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxY = ring.Max(p => p.Y);
        return Math.Min(maxX - minX, maxY - minY);
    }

    static Path64 ToPath64(IReadOnlyList<Point2> pts)
    {
        var path = new Path64(pts.Count);
        foreach (var p in pts)
            path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        return path;
    }
}
