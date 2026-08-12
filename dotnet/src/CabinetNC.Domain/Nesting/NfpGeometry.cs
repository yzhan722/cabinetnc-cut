namespace CabinetNC.Domain.Nesting;

using Clipper2Lib;

/// <summary>
/// Clipper2 Minkowski-difference NFP helpers.
/// Reference point = lower-left of the moving polygon's AABB (matches <see cref="NestTransform"/>).
/// </summary>
public static class NfpGeometry
{
    public const double Scale = 1000;

    /// <summary>
    /// NFP of fixed obstacle vs moving part: translations of moving's reference that cause overlap.
    /// Uses <c>MinkowskiDiff(moving, fixed) = fixed ⊕ (-moving)</c>.
    /// </summary>
    public static Paths64 ComputeNfp(Path64 fixedObstacle, Path64 movingLocal)
    {
        if (fixedObstacle.Count < 3 || movingLocal.Count < 3)
            return [];
        var a = EnsurePositive(Simplify(fixedObstacle, 32));
        var b = EnsurePositive(Simplify(movingLocal, 32));
        if (a.Count < 3 || b.Count < 3) return [];
        try
        {
            return Clipper.MinkowskiDiff(b, a, true);
        }
        catch
        {
            return [];
        }
    }

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
        // Largest area component
        return inflated.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
    }

    public static bool ReferenceForbidden(double xMm, double yMm, Paths64 nfps)
    {
        var pt = new Point64((long)Math.Round(xMm * Scale), (long)Math.Round(yMm * Scale));
        foreach (var nfp in nfps)
        {
            if (nfp.Count < 3) continue;
            if (Clipper.PointInPolygon(pt, nfp) == PointInPolygonResult.IsInside)
                return true;
        }
        return false;
    }

    public static IEnumerable<(double X, double Y)> CandidateReferences(Paths64 nfps, double borderMm, int max)
    {
        var seen = new HashSet<(long, long)>();
        var list = new List<(double X, double Y)>(max);
        void Add(double x, double y)
        {
            if (list.Count >= max) return;
            if (x < borderMm - 1e-6 || y < borderMm - 1e-6) return;
            var key = ((long)Math.Round(x * 100), (long)Math.Round(y * 100));
            if (!seen.Add(key)) return;
            list.Add((x, y));
        }

        Add(borderMm, borderMm);
        foreach (var nfp in nfps)
        {
            if (nfp.Count < 2) continue;
            for (var i = 0; i < nfp.Count; i++)
            {
                var a = nfp[i];
                var b = nfp[(i + 1) % nfp.Count];
                Add(a.X / Scale, a.Y / Scale);
                Add((a.X + b.X) * 0.5 / Scale, (a.Y + b.Y) * 0.5 / Scale);
                // nudge slightly outside along edge normal approximation (toward lower-left bias)
                var mx = (a.X + b.X) * 0.5 / Scale;
                var my = (a.Y + b.Y) * 0.5 / Scale;
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
