namespace CabinetNC.Domain.Manufacturing;

using Clipper2Lib;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

/// <summary>
/// Paste-point for the 60×40 mm shop label (printer orientation is fixed, no 90°). Prefers the outline centroid;
/// if that lands in a keep-out or cannot hold the sticker with interior
/// clearance, picks an interior point of the remaining solid — never the
/// legal-area rim.
/// </summary>
public readonly record struct LabelAnchor(
    double LocalX,
    double LocalY,
    double WidthMm,
    double HeightMm,
    bool FitsComfortably,
    bool FitsAtAll);

public static class LabelAnchorFinder
{
    public const double WidthMm = 60;
    public const double HeightMm = 40;
    public const double KeepOutInflateMm = 1;
    public const double InteriorClearanceMm = 10;
    public const double LargeThroughHoleMm = 15;
    const double Scale = 1000;
    const double AreaTol = 0.98;

    static readonly object CacheLock = new();
    static readonly Dictionary<(string Id, int Rot, int Sig), LabelAnchor> Cache = new();

    public static LabelAnchor Find(
        Panel panel,
        double rotationDeg = 0,
        (double X, double Y)? localOverride = null)
    {
        var rot = ((int)Math.Round(rotationDeg) % 360 + 360) % 360;
        if (localOverride is { } ov)
            return ApplyOverride(panel, rot, ov.X, ov.Y);

        var sig = Signature(panel);
        var key = (panel.PanelId, rot, sig ^ panel.Features.Count);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
        }

        var found = ComputeAuto(panel, rot);
        lock (CacheLock)
        {
            if (Cache.Count >= 4000)
            {
                var drop = Cache.Count / 4;
                foreach (var stale in Cache.Keys.Take(drop).ToList())
                    Cache.Remove(stale);
            }
            Cache[key] = found;
        }
        return found;
    }

    static LabelAnchor ComputeAuto(Panel panel, int rotationDeg)
    {
        if (!TryWorld(panel, rotationDeg, out var world))
            return new LabelAnchor(0, 0, WidthMm, HeightMm, false, false);
        if (world.Solid.Count == 0)
            return ToAnchor(world, world.Centroid.X, world.Centroid.Y, false, false);

        if (TryBest(world.Solid, world.Forbidden, world.Centroid, InteriorClearanceMm, out var comfortable))
            return ToAnchor(world, comfortable.X, comfortable.Y, true, true);

        var interior = MostInterior(world.Solid, world.Forbidden, world.Centroid);
        var fits = LabelFits(world.Solid, world.Forbidden, interior.X, interior.Y, WidthMm, HeightMm, 0);
        return ToAnchor(world, interior.X, interior.Y, false, fits);
    }

    static LabelAnchor ApplyOverride(Panel panel, int rotationDeg, double localX, double localY)
    {
        if (!TryWorld(panel, rotationDeg, out var world))
            return new LabelAnchor(localX, localY, WidthMm, HeightMm, false, false);

        var (rx, ry) = NestTransform.ToSheet(localX, localY, world.Bounds, 0, 0, rotationDeg);
        if (PointAllowed(world.Solid, world.Forbidden, rx, ry))
        {
            if (LabelFits(world.Solid, world.Forbidden, rx, ry, WidthMm, HeightMm, InteriorClearanceMm))
                return ToAnchor(world, rx, ry, true, true);
            if (LabelFits(world.Solid, world.Forbidden, rx, ry, WidthMm, HeightMm, 0))
                return ToAnchor(world, rx, ry, false, true);
        }

        if (TryBest(world.Solid, world.Forbidden, (rx, ry), InteriorClearanceMm, out var comfortable))
            return ToAnchor(world, comfortable.X, comfortable.Y, true, true);
        if (TryBest(world.Solid, world.Forbidden, (rx, ry), 0, out var tight))
            return ToAnchor(world, tight.X, tight.Y, false, true);

        var interior = MostInterior(world.Solid, world.Forbidden, (rx, ry));
        var fits = LabelFits(world.Solid, world.Forbidden, interior.X, interior.Y, WidthMm, HeightMm, 0);
        return ToAnchor(world, interior.X, interior.Y, false, fits);
    }

    readonly record struct World(
        LocalBounds Bounds,
        int Rot,
        Paths64 Solid,
        Paths64 Forbidden,
        (double X, double Y) Centroid);

    static bool TryWorld(Panel panel, int rotationDeg, out World world)
    {
        world = default;
        var localPts = panel.Outline.Points;
        if (localPts.Count < 3) return false;
        var bounds = NestTransform.BoundsOf(panel);
        var outline = RotateRing(localPts, bounds, rotationDeg);
        var keepOuts = CollectKeepOuts(panel)
            .Select(ring => RotateRing(ring, bounds, rotationDeg))
            .Where(ring => ring.Count >= 3)
            .ToList();
        world = new World(
            bounds,
            rotationDeg,
            BuildSolid(outline, keepOuts),
            ToKeepOutPaths(keepOuts),
            PolygonCentroid(outline));
        return true;
    }

    static LabelAnchor ToAnchor(World world, double rx, double ry, bool comfortable, bool fits)
    {
        var (lx, ly) = ToLocal(rx, ry, world.Bounds, world.Rot);
        return new LabelAnchor(lx, ly, WidthMm, HeightMm, comfortable, fits);
    }

    static bool TryBest(
        Paths64 solid,
        Paths64 forbidden,
        (double X, double Y) centroid,
        double extraInset,
        out (double X, double Y, double W, double H) pick)
    {
        pick = default;
        var best = pick;
        var bestD = double.PositiveInfinity;
        var found = false;

        void Consider(double x, double y)
        {
            if (!PointAllowed(solid, forbidden, x, y))
                return;
            if (!LabelFits(solid, forbidden, x, y, WidthMm, HeightMm, extraInset))
                return;
            var w = WidthMm;
            var h = HeightMm;
            var dx = x - centroid.X;
            var dy = y - centroid.Y;
            var d = dx * dx + dy * dy;
            if (d >= bestD) return;
            bestD = d;
            best = (x, y, w, h);
            found = true;
        }

        Consider(centroid.X, centroid.Y);

        foreach (var island in Inset(solid, extraInset + 2))
        {
            var pts = ToPoints(island);
            if (pts.Count < 3) continue;
            var c = PolygonCentroid(pts);
            Consider(c.X, c.Y);
        }

        if (found)
        {
            pick = best;
            return true;
        }

        var box = BoundsOf(solid);
        if (box.MaxX <= box.MinX || box.MaxY <= box.MinY)
            return false;
        var span = Math.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
        var step = span > 1600 ? 20.0 : span > 800 ? 14.0 : 10.0;
        for (var x = box.MinX + step; x < box.MaxX; x += step)
        {
            for (var y = box.MinY + step; y < box.MaxY; y += step)
                Consider(x, y);
        }

        if (found)
            pick = best;
        return found;
    }

    static (double X, double Y) MostInterior(Paths64 solid, Paths64 forbidden, (double X, double Y) centroid)
    {
        foreach (var inset in new[] { 24.0, 18.0, 12.0, 8.0, 4.0 })
        {
            var inner = Inset(solid, inset);
            if (inner.Count == 0) continue;
            var best = centroid;
            var bestD = double.PositiveInfinity;
            var any = false;
            foreach (var island in inner)
            {
                var pts = ToPoints(island);
                if (pts.Count < 3) continue;
                var c = PolygonCentroid(pts);
                if (!PointAllowed(inner, forbidden, c.X, c.Y)) continue;
                var dx = c.X - centroid.X;
                var dy = c.Y - centroid.Y;
                var d = dx * dx + dy * dy;
                if (d >= bestD) continue;
                bestD = d;
                best = c;
                any = true;
            }
            if (any) return best;
        }

        var fallback = ToPoints(solid.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First());
        return fallback.Count >= 3 ? PolygonCentroid(fallback) : centroid;
    }

    static bool LabelFits(
        Paths64 solid,
        Paths64 forbidden,
        double cx,
        double cy,
        double w,
        double h,
        double extraInset)
    {
        var hw = w * 0.5 + extraInset;
        var hh = h * 0.5 + extraInset;
        if (hw <= 0 || hh <= 0) return false;
        var rect = RectPath(cx - hw, cy - hh, cx + hw, cy + hh);
        if (forbidden.Count > 0)
        {
            var clash = Clipper.Intersect([rect], forbidden, FillRule.NonZero);
            if (clash.Any(p => Math.Abs(Clipper.Area(p)) > Scale * Scale * 0.25))
                return false;
        }
        var hit = Clipper.Intersect([rect], solid, FillRule.NonZero);
        if (hit.Count == 0) return false;
        var got = hit.Sum(p => Math.Abs(Clipper.Area(p)));
        var want = (2 * hw) * (2 * hh) * Scale * Scale;
        return got >= want * AreaTol;
    }

    static bool PointAllowed(Paths64 solid, Paths64 forbidden, double x, double y) =>
        PointInside(solid, x, y) && !PointInsideAny(forbidden, x, y);

    static bool PointInsideAny(Paths64 paths, double x, double y)
    {
        var pt = new Point64((long)Math.Round(x * Scale), (long)Math.Round(y * Scale));
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var hit = Clipper.PointInPolygon(pt, path);
            if (hit is PointInPolygonResult.IsInside or PointInPolygonResult.IsOn)
                return true;
        }
        return false;
    }

    static Paths64 ToKeepOutPaths(List<IReadOnlyList<(double X, double Y)>> keepOuts)
    {
        var clips = new Paths64();
        foreach (var ring in keepOuts)
        {
            var path = EnsurePositive(ToPath(ring));
            if (path.Count < 3) continue;
            var grown = Clipper.InflatePaths(
                [path], KeepOutInflateMm * Scale, JoinType.Round, EndType.Polygon);
            if (grown.Count == 0)
                clips.Add(path);
            else
                clips.AddRange(grown.Select(EnsurePositive));
        }
        return clips;
    }

    static Paths64 BuildSolid(IReadOnlyList<(double X, double Y)> outline, List<IReadOnlyList<(double X, double Y)>> keepOuts)
    {
        var subject = new Paths64 { EnsurePositive(ToPath(outline)) };
        var clips = new Paths64();
        foreach (var ring in keepOuts)
        {
            var path = EnsurePositive(ToPath(ring));
            if (path.Count < 3) continue;
            var grown = Clipper.InflatePaths(
                [path], KeepOutInflateMm * Scale, JoinType.Round, EndType.Polygon);
            if (grown.Count == 0)
                clips.Add(path);
            else
                clips.AddRange(grown.Select(EnsurePositive));
        }

        var solid = clips.Count == 0
            ? subject
            : Clipper.Difference(subject, clips, FillRule.NonZero);
        return new Paths64(solid.Where(p => p.Count >= 3 && Math.Abs(Clipper.Area(p)) > Scale * Scale));
    }

    static List<IReadOnlyList<Point2>> CollectKeepOuts(Panel panel)
    {
        var rings = new List<IReadOnlyList<Point2>>();
        foreach (var f in panel.Features)
        {
            if (PanelEdit.IsCutout(f) && RingOf(f) is { Count: >= 3 } cut)
            {
                rings.Add(cut);
                continue;
            }

            if (IsSlotKeepOut(f) || IsLedKeepOut(f))
            {
                var slot = SlotRing(f);
                if (slot.Count >= 3)
                    rings.Add(slot);
                continue;
            }

            if (ClearanceToolPick.IsHingeFeature(f)
                && ClearanceToolPick.CupOutline(f) is { Count: >= 3 } cup)
            {
                rings.Add(cup.Select(p => new Point2(p.X, p.Y)).ToList());
                continue;
            }

            if (PanelEdit.IsHole(f)
                && f.Through
                && f.DiameterMm is >= LargeThroughHoleMm)
            {
                rings.Add(Circle(f.X, f.Y, f.DiameterMm.Value * 0.5));
            }
        }
        return rings;
    }

    static bool IsSlotKeepOut(PanelFeature f)
    {
        // Red nest slots are all grooves — tagged or not (full / half / unmarked dado).
        if (PanelEdit.IsGroove(f)) return true;
        var blob = $"{f.Purpose} {f.Kind} {f.FeatureId}";
        if (blob.Contains("slot", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("半槽", StringComparison.Ordinal)
            || blob.Contains("通槽", StringComparison.Ordinal))
            return true;
        return LockSlotGeometry.IsLockIntent(f.Purpose, f.Kind);
    }

    static bool IsLedKeepOut(PanelFeature f)
    {
        var blob = $"{f.Purpose} {f.Kind} {f.FeatureId}";
        return blob.Contains("led", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("灯槽", StringComparison.Ordinal)
            || blob.Contains("灯带", StringComparison.Ordinal);
    }

    static IReadOnlyList<Point2> SlotRing(PanelFeature f)
    {
        var drawn = GrooveGeometry.DisplayOutline(f);
        if (drawn.Count >= 3) return drawn;
        if (RingOf(f) is { Count: >= 3 } ring) return ring;
        var width = f.WidthMm is > 1e-9
            ? f.WidthMm.Value
            : GrooveGeometry.InferWidthMm(f.Path, f.Profile);
        return GrooveGeometry.OutlineFromCenterline(f.Path, width);
    }

    static IReadOnlyList<Point2>? RingOf(PanelFeature f) =>
        f.Path is { Count: >= 3 } path ? path
        : f.Profile is { Count: >= 3 } profile ? profile
        : null;

    static List<Point2> Circle(double x, double y, double r, int n = 32)
    {
        var pts = new List<Point2>(n);
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            pts.Add(new Point2(x + r * Math.Cos(a), y + r * Math.Sin(a)));
        }
        return pts;
    }

    static IReadOnlyList<(double X, double Y)> RotateRing(
        IReadOnlyList<Point2> ring,
        LocalBounds bounds,
        int rotationDeg)
    {
        var list = new List<(double X, double Y)>(ring.Count);
        foreach (var p in ring)
            list.Add(NestTransform.ToSheet(p.X, p.Y, bounds, 0, 0, rotationDeg));
        return list;
    }

    static (double X, double Y) ToLocal(
        double rotatedX,
        double rotatedY,
        LocalBounds bounds,
        int rotationDeg) =>
        NestTransform.FromSheet(rotatedX, rotatedY, bounds, 0, 0, rotationDeg);

    static (double X, double Y) PolygonCentroid(IReadOnlyList<(double X, double Y)> ring)
    {
        if (ring.Count == 0) return (0, 0);
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        a *= 0.5;
        if (Math.Abs(a) < 1e-9)
            return (ring.Average(p => p.X), ring.Average(p => p.Y));
        return (cx / (6 * a), cy / (6 * a));
    }

    static Paths64 Inset(Paths64 solid, double mm)
    {
        if (mm <= 0) return solid;
        var inset = Clipper.InflatePaths(solid, -mm * Scale, JoinType.Round, EndType.Polygon);
        return new Paths64(inset.Where(p => p.Count >= 3 && Math.Abs(Clipper.Area(p)) > Scale * Scale));
    }

    static bool PointInside(Paths64 paths, double x, double y)
    {
        var pt = new Point64((long)Math.Round(x * Scale), (long)Math.Round(y * Scale));
        var inside = false;
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var hit = Clipper.PointInPolygon(pt, path);
            if (hit == PointInPolygonResult.IsInside)
                inside = !inside;
            else if (hit == PointInPolygonResult.IsOn)
                return false;
        }
        return inside;
    }

    static (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(Paths64 paths)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var path in paths)
        {
            foreach (var p in path)
            {
                var x = p.X / Scale;
                var y = p.Y / Scale;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return (minX, minY, maxX, maxY);
    }

    static List<(double X, double Y)> ToPoints(Path64 path)
    {
        var list = new List<(double X, double Y)>(path.Count);
        foreach (var p in path)
            list.Add((p.X / Scale, p.Y / Scale));
        return list;
    }

    static Path64 ToPath(IReadOnlyList<(double X, double Y)> pts)
    {
        var path = new Path64(pts.Count);
        foreach (var p in pts)
            path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        return path;
    }

    static Path64 RectPath(double minX, double minY, double maxX, double maxY) =>
    [
        new((long)Math.Round(minX * Scale), (long)Math.Round(minY * Scale)),
        new((long)Math.Round(maxX * Scale), (long)Math.Round(minY * Scale)),
        new((long)Math.Round(maxX * Scale), (long)Math.Round(maxY * Scale)),
        new((long)Math.Round(minX * Scale), (long)Math.Round(maxY * Scale)),
    ];

    static Path64 EnsurePositive(Path64 path) =>
        path.Count >= 3 && Clipper.Area(path) < 0 ? Clipper.ReversePath(path) : path;

    static int Signature(Panel panel)
    {
        var h = new System.HashCode();
        foreach (var p in panel.Outline.Points)
        {
            h.Add(Math.Round(p.X, 3));
            h.Add(Math.Round(p.Y, 3));
        }
        foreach (var f in panel.Features)
        {
            h.Add(f.FeatureId);
            h.Add(f.Kind);
            h.Add(f.Purpose);
            h.Add(f.Through);
            h.Add(Math.Round(f.X, 3));
            h.Add(Math.Round(f.Y, 3));
            h.Add(f.DiameterMm);
            h.Add(f.WidthMm);
            if (f.Path is { Count: > 0 } path)
            {
                h.Add(path.Count);
                h.Add(Math.Round(path[0].X, 3));
                h.Add(Math.Round(path[^1].Y, 3));
            }
        }
        return h.ToHashCode();
    }
}
