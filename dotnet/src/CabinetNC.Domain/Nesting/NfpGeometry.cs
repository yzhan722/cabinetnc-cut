namespace CabinetNC.Domain.Nesting;

using Clipper2Lib;

/// <summary>
/// Clipper2 Minkowski-difference NFP helpers.
/// Reference point = lower-left of the moving polygon's AABB (matches <see cref="NestTransform"/>).
/// Concave parts are split into convex pieces, then pairwise NFPs are unioned.
/// </summary>
public static class NfpGeometry
{
    public const double Scale = 1000;
    const int MaxPreparePoints = 48;
    const double SlideStepMm = 8;
    const int MaxSlidePerEdge = 10;

    /// <summary>
    /// NFP of fixed obstacle vs moving part: translations of moving's reference that cause overlap.
    /// Uses <c>MinkowskiDiff(moving, fixed) = fixed ⊕ (-moving)</c> on convex pieces.
    /// </summary>
    public static Paths64 ComputeNfp(Path64 fixedObstacle, Path64 movingLocal)
    {
        var a = Prepare(fixedObstacle);
        var b = Prepare(movingLocal);
        if (a.Count < 3 || b.Count < 3) return [];

        var fixedParts = NfpConvexDecompose.Decompose(a);
        var movingParts = NfpConvexDecompose.Decompose(b);
        if (fixedParts.Count == 0 || movingParts.Count == 0) return [];

        var acc = new Paths64();
        foreach (var f in fixedParts)
        {
            foreach (var m in movingParts)
            {
                if (f.Count < 3 || m.Count < 3) continue;
                try
                {
                    foreach (var path in Clipper.MinkowskiDiff(m, f, true))
                    {
                        if (path.Count >= 3)
                            acc.Add(path);
                    }
                }
                catch
                {
                    // skip this pair; remaining pieces still constrain the NFP
                }
            }
        }

        if (acc.Count == 0) return [];
        try
        {
            var united = Clipper.Union(acc, FillRule.NonZero);
            return united.Count > 0 ? united : acc;
        }
        catch
        {
            return acc;
        }
    }

    public static IReadOnlyList<Path64> DecomposeConvex(Path64 path) =>
        NfpConvexDecompose.Decompose(path);

    public static Path64 ToPath(IReadOnlyList<(double X, double Y)> points)
    {
        var path = new Path64(points.Count);
        foreach (var p in points)
            path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        return path;
    }

    public static Path64 Inflate(Path64 path, double mm)
    {
        if (mm <= 0 || path.Count < 3) return path;
        var inflated = Clipper.InflatePaths([path], mm * Scale, JoinType.Round, EndType.Polygon);
        if (inflated.Count == 0) return path;
        return inflated.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
    }

    public static Path64 Clean(Path64 path)
    {
        if (path.Count == 0) return path;
        const long eps2 = 50L * 50; // 0.05 mm
        var tight = new Path64(path.Count);
        foreach (var p in path)
        {
            if (tight.Count > 0)
            {
                var dx = p.X - tight[^1].X;
                var dy = p.Y - tight[^1].Y;
                if (dx * dx + dy * dy < eps2) continue;
            }
            tight.Add(p);
        }

        if (tight.Count >= 2)
        {
            var dx = tight[0].X - tight[^1].X;
            var dy = tight[0].Y - tight[^1].Y;
            if (dx * dx + dy * dy < eps2)
                tight.RemoveAt(tight.Count - 1);
        }

        return EnsurePositive(tight);
    }

    public static Path64 Prepare(Path64 path, int maxPoints = MaxPreparePoints)
    {
        var clean = Clean(path);
        if (clean.Count <= maxPoints) return clean;
        try
        {
            var simplified = Clipper.SimplifyPaths([clean], 0.4 * Scale);
            if (simplified.Count > 0)
            {
                var best = simplified.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
                if (best.Count >= 3 && best.Count <= maxPoints)
                    return EnsurePositive(best);
                if (best.Count > maxPoints)
                    return Simplify(EnsurePositive(best), maxPoints);
            }
        }
        catch
        {
            // fall through to stride simplify
        }
        return Simplify(clean, maxPoints);
    }

    public static bool IsConvex(Path64 path)
    {
        if (path.Count < 3) return false;
        var n = path.Count;
        var sign = 0;
        for (var i = 0; i < n; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % n];
            var c = path[(i + 2) % n];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross == 0) continue;
            var s = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>
    /// True when the moving reference sits strictly inside an NFP outer
    /// (holes from union are legal islands).
    /// </summary>
    public static bool ReferenceForbidden(double xMm, double yMm, Paths64 nfps)
    {
        var pt = new Point64((long)Math.Round(xMm * Scale), (long)Math.Round(yMm * Scale));
        var inOuter = false;
        var inHole = false;
        foreach (var nfp in nfps)
        {
            if (nfp.Count < 3) continue;
            if (Clipper.PointInPolygon(pt, nfp) != PointInPolygonResult.IsInside)
                continue;
            if (Clipper.Area(nfp) >= 0) inOuter = true;
            else inHole = true;
        }
        return inOuter && !inHole;
    }

    public static IEnumerable<(double X, double Y)> CandidateReferences(Paths64 nfps, double borderMm, int max) =>
        CandidateReferences(nfps, borderMm, borderMm, max);

    public static IEnumerable<(double X, double Y)> CandidateReferences(
        Paths64 nfps, double minX, double minY, int max)
    {
        var seen = new HashSet<(long, long)>();
        var list = new List<(double X, double Y)>(max);
        void Add(double x, double y)
        {
            if (list.Count >= max) return;
            if (x < minX - 1e-6 || y < minY - 1e-6) return;
            var key = ((long)Math.Round(x * 100), (long)Math.Round(y * 100));
            if (!seen.Add(key)) return;
            list.Add((x, y));
        }

        Add(minX, minY);
        foreach (var nfp in nfps)
        {
            if (nfp.Count < 2) continue;
            for (var i = 0; i < nfp.Count; i++)
            {
                var a = nfp[i];
                var b = nfp[(i + 1) % nfp.Count];
                var ax = a.X / Scale;
                var ay = a.Y / Scale;
                var bx = b.X / Scale;
                var by = b.Y / Scale;
                Add(ax, ay);

                var dx = bx - ax;
                var dy = by - ay;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.2) continue;

                var samples = (int)Math.Min(MaxSlidePerEdge, Math.Max(1, Math.Floor(len / SlideStepMm)));
                for (var s = 1; s <= samples; s++)
                {
                    var t = s / (double)(samples + 1);
                    Add(ax + dx * t, ay + dy * t);
                }

                var mx = (ax + bx) * 0.5;
                var my = (ay + by) * 0.5;
                Add(mx + 0.05, my);
                Add(mx, my + 0.05);
                Add(mx - 0.05, my);
                Add(mx, my - 0.05);
            }
        }

        return list
            .OrderBy(p => p.Y)
            .ThenBy(p => p.X)
            .Take(max);
    }

    public static Path64 Simplify(Path64 path, int maxPoints)
    {
        if (path.Count <= maxPoints) return path;
        var step = (double)(path.Count - 1) / (maxPoints - 1);
        var simple = new Path64(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            var idx = (int)Math.Round(i * step);
            if (idx >= path.Count) idx = path.Count - 1;
            simple.Add(path[idx]);
        }
        return simple;
    }

    public static Path64 EnsurePositive(Path64 path)
    {
        if (path.Count < 3) return path;
        return Clipper.Area(path) < 0 ? Clipper.ReversePath(path) : path;
    }
}
