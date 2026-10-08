namespace CabinetNC.Verify;

using Clipper2Lib;

/// <summary>Clipper2 helpers in sheet millimetres (1 unit = 1 µm).</summary>
internal static class Geo
{
    public const double Scale = 1000;
    const double AreaScale = Scale * Scale;

    public static Point64 P(double x, double y) =>
        new((long)Math.Round(x * Scale), (long)Math.Round(y * Scale));

    public static Path64 Path(IEnumerable<(double X, double Y)> pts)
    {
        var path = new Path64();
        foreach (var (x, y) in pts)
            path.Add(P(x, y));
        return path;
    }

    /// <summary>Closed ring → normalised polygon set (orientation fixed, self-overlap merged).</summary>
    public static Paths64 Polygon(IEnumerable<(double X, double Y)> ring)
    {
        var path = Path(ring);
        if (path.Count > 1 && path[0] == path[^1]) path.RemoveAt(path.Count - 1);
        if (path.Count < 3) return [];
        return Clipper.Union(new Paths64 { path }, FillRule.NonZero);
    }

    public static Paths64 Union(Paths64 a, Paths64 b) => Clipper.Union(a, b, FillRule.NonZero);
    public static Paths64 Union(Paths64 a) => Clipper.Union(a, FillRule.NonZero);
    public static Paths64 Union(IEnumerable<Paths64> many)
    {
        var all = new Paths64();
        foreach (var p in many) all.AddRange(p);
        return all.Count == 0 ? [] : Clipper.Union(all, FillRule.NonZero);
    }

    public static Paths64 Diff(Paths64 subject, Paths64 clip) =>
        subject.Count == 0 ? [] : clip.Count == 0 ? subject : Clipper.Difference(subject, clip, FillRule.NonZero);

    public static Paths64 Inter(Paths64 a, Paths64 b) =>
        a.Count == 0 || b.Count == 0 ? [] : Clipper.Intersect(a, b, FillRule.NonZero);

    /// <summary>Grow (+) or shrink (−) a polygon set with round joins.</summary>
    public static Paths64 Offset(Paths64 polys, double mm, double arcChordMm = 0.02)
    {
        if (polys.Count == 0) return [];
        if (Math.Abs(mm) < 1e-9) return polys;
        var r = Clipper.InflatePaths(polys, mm * Scale, JoinType.Round, EndType.Polygon, 2.0, arcChordMm * Scale);
        return r.Count == 0 ? [] : Clipper.Union(r, FillRule.NonZero);
    }

    /// <summary>Open polyline swept by a disc of radius <paramref name="rMm"/>.</summary>
    public static Paths64 SweepRound(Path64 line, double rMm, double arcChordMm = 0.02)
    {
        if (line.Count == 0 || rMm <= 1e-9) return [];
        if (line.Count == 1) return Disc(line[0].X / Scale, line[0].Y / Scale, rMm);
        var r = Clipper.InflatePaths(new Paths64 { line }, rMm * Scale, JoinType.Round, EndType.Round, 2.0, arcChordMm * Scale);
        return r.Count == 0 ? [] : Clipper.Union(r, FillRule.NonZero);
    }

    /// <summary>Open polyline → strip of half-width <paramref name="halfMm"/> with square (butt) ends.</summary>
    public static Paths64 StripButt(Path64 line, double halfMm)
    {
        if (line.Count < 2 || halfMm <= 1e-9) return [];
        var r = Clipper.InflatePaths(new Paths64 { line }, halfMm * Scale, JoinType.Miter, EndType.Butt, 4.0);
        return r.Count == 0 ? [] : Clipper.Union(r, FillRule.NonZero);
    }

    public static Paths64 Disc(double x, double y, double r, int segments = 64)
    {
        if (r <= 1e-9) return [];
        var path = new Path64(segments);
        for (var i = 0; i < segments; i++)
        {
            var a = 2 * Math.PI * i / segments;
            path.Add(P(x + r * Math.Cos(a), y + r * Math.Sin(a)));
        }
        return new Paths64 { path };
    }

    /// <summary>Thin rectangle centred at (x,y), along unit (ux,uy), half-length L, half-thickness t.</summary>
    public static Paths64 Probe(double x, double y, double ux, double uy, double halfLenMm, double halfThickMm)
    {
        var nx = -uy;
        var ny = ux;
        var ring = new[]
        {
            (x - ux * halfLenMm + nx * halfThickMm, y - uy * halfLenMm + ny * halfThickMm),
            (x + ux * halfLenMm + nx * halfThickMm, y + uy * halfLenMm + ny * halfThickMm),
            (x + ux * halfLenMm - nx * halfThickMm, y + uy * halfLenMm - ny * halfThickMm),
            (x - ux * halfLenMm - nx * halfThickMm, y - uy * halfLenMm - ny * halfThickMm),
        };
        return Polygon(ring);
    }

    public static double AreaMm2(Paths64 polys) => polys.Count == 0 ? 0 : Clipper.Area(polys) / AreaScale;
    public static double AreaMm2(Path64 path) => Clipper.Area(path) / AreaScale;

    /// <summary>Outer rings (positive area) — good enough to locate and size a defect.</summary>
    public static IEnumerable<Path64> Pieces(Paths64 polys, double minAreaMm2) =>
        polys.Where(p => p.Count >= 3 && AreaMm2(p) >= minAreaMm2)
            .OrderByDescending(AreaMm2);

    public static (double X, double Y) Centroid(Path64 path)
    {
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < path.Count; i++)
        {
            var p = path[i];
            var q = path[(i + 1) % path.Count];
            var cross = (double)p.X * q.Y - (double)q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(a) < 1e-9)
            return (path.Average(p => p.X) / Scale, path.Average(p => p.Y) / Scale);
        a *= 0.5;
        return (cx / (6 * a) / Scale, cy / (6 * a) / Scale);
    }

    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(Paths64 polys)
    {
        var r = Clipper.GetBounds(polys);
        return (r.left / Scale, r.top / Scale, r.right / Scale, r.bottom / Scale);
    }

    public static double ShortSpanMm(Paths64 polys)
    {
        if (polys.Count == 0) return 0;
        var (minX, minY, maxX, maxY) = Bounds(polys);
        return Math.Min(maxX - minX, maxY - minY);
    }

    public static double LongSpanMm(Paths64 polys)
    {
        if (polys.Count == 0) return 0;
        var (minX, minY, maxX, maxY) = Bounds(polys);
        return Math.Max(maxX - minX, maxY - minY);
    }

    public static bool Contains(Paths64 region, double x, double y)
    {
        var pt = P(x, y);
        var inside = 0;
        foreach (var path in region)
        {
            var hit = Clipper.PointInPolygon(pt, path);
            if (hit == PointInPolygonResult.IsOutside) continue;
            inside += Clipper.Area(path) >= 0 ? 1 : -1;
        }
        return inside > 0;
    }

    /// <summary>Approximate widest inscribed gap of a piece: 2 × the shrink at which it vanishes.</summary>
    public static double GapMm(Path64 piece)
    {
        var polys = new Paths64 { piece };
        var hi = ShortSpanMm(polys) * 0.5 + 0.01;
        var lo = 0d;
        for (var i = 0; i < 7; i++)
        {
            var mid = (lo + hi) * 0.5;
            var shrunk = Clipper.InflatePaths(polys, -mid * Scale, JoinType.Round, EndType.Polygon);
            if (shrunk.Count == 0 || AreaMm2(shrunk) <= 1e-6) hi = mid;
            else lo = mid;
        }
        return Math.Round(2 * lo, 3);
    }

    /// <summary>Distance from a point to the boundary of a polygon set.</summary>
    public static double DistanceToBoundary(Paths64 polys, double x, double y)
    {
        var best = double.PositiveInfinity;
        var px = x * Scale;
        var py = y * Scale;
        foreach (var path in polys)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                var d = SegDist(px, py, a.X, a.Y, b.X, b.Y);
                if (d < best) best = d;
            }
        }
        return best / Scale;
    }

    static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        var vx = bx - ax;
        var vy = by - ay;
        var len2 = vx * vx + vy * vy;
        var t = len2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0, 1);
        var qx = ax + t * vx;
        var qy = ay + t * vy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }
}
