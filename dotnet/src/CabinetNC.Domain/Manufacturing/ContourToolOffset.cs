namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;
using Clipper2Lib;

/// <summary>
/// Clipper2 contour compensation in sheet-space millimetres.
/// Outer profiles use round tool-centre joins (R = tool radius), matching
/// Carveco while the finished part corner remains sharp. Inner cutouts keep
/// miter joins so tool compensation does not double the unavoidable inside R.
/// </summary>
public static class ContourToolOffset
{
    const double Scale = 10000;
    const double MiterLimit = 2;

    public static IReadOnlyList<CutOp> Apply(IEnumerable<CutOp> ops, double offsetMm)
    {
        if (Math.Abs(offsetMm) < 1e-9) return ops.ToList();

        return ops.Select(op =>
        {
            if (op.Op != "contour" || op.Path is not { Count: >= 3 } path)
                return op;

            // Lock / stadium first. CadPath arc offset used to enlarge the
            // CCW end-caps (R8 → R13) while the sides inset — a dumbbell.
            if (op.FeatureId is not null
                && TryInsetCapsule(path, offsetMm, op.RotationDeg, out var capsule, out var capsuleCad))
            {
                var oriented = ClimbCut.OrientClosed(capsule, inner: true).ToList();
                var cad = capsuleCad.Count > 0
                    ? CadPath.OrientClosed(capsuleCad, inner: true)
                    : null;
                return op with { Path = oriented, CadPath = cad };
            }

            if (op.CadPath is { Count: > 0 } sourceCad
                && CadPath.TryOffset(
                    sourceCad,
                    op.FeatureId is null ? offsetMm : -offsetMm,
                    roundConvex: op.FeatureId is null,
                    out var offsetCad)
                && offsetCad.Count > 0)
            {
                var orientedCad = CadPath.OrientClosed(offsetCad, inner: op.FeatureId is not null);
                var sampled = CadPath.ToPolyline(orientedCad, 0.4);
                sampled = ClimbCut.OrientClosed(sampled, inner: op.FeatureId is not null).ToList();
                return op with { Path = sampled, CadPath = orientedCad };
            }

            var source = new Path64(path.Count);
            foreach (var p in path)
                source.Add(new Point64(
                    (long)Math.Round(p.X * Scale),
                    (long)Math.Round(p.Y * Scale)));

            var join = op.FeatureId is null ? JoinType.Round : JoinType.Miter;
            var inflated = Clipper.InflatePaths(
                new Paths64 { source },
                (op.FeatureId is null ? offsetMm : -offsetMm) * Scale,
                join,
                EndType.Polygon,
                MiterLimit);
            var best = inflated
                .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
                .FirstOrDefault();
            if (best is null || best.Count < 3) return op;

            var pts = best
                .Select(p => (p.X / Scale, p.Y / Scale))
                .ToList();
            pts = ClimbCut.OrientClosed(pts, inner: op.FeatureId is not null).ToList();
            return op with { Path = pts };
        }).ToList();
    }

    static bool TryInsetCapsule(
        IReadOnlyList<(double X, double Y)> source,
        double insetMm,
        double rotationDeg,
        out IReadOnlyList<(double X, double Y)> inset) =>
        TryInsetCapsule(source, insetMm, rotationDeg, out inset, out _);

    static bool TryInsetCapsule(
        IReadOnlyList<(double X, double Y)> source,
        double insetMm,
        double rotationDeg,
        out IReadOnlyList<(double X, double Y)> inset,
        out IReadOnlyList<CadSegment> cad)
    {
        inset = [];
        cad = [];
        if (insetMm <= 0 || source.Count < 8)
            return false;

        var angle = rotationDeg * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        (double X, double Y) Rotate((double X, double Y) p, bool inverse) =>
            inverse
                ? (p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos)
                : (p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);

        // Nesting may rotate a lock slot by an arbitrary angle. Undo that
        // placement rotation so the AABB is again the slot's local AABB.
        var points = source.Select(p => Rotate(p, inverse: true)).ToList();
        if (points.Count >= 2
            && Math.Abs(points[0].X - points[^1].X) < 1e-6
            && Math.Abs(points[0].Y - points[^1].Y) < 1e-6)
            points.RemoveAt(points.Count - 1);
        if (points.Count < 8)
            return false;

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        if (shortSide <= insetMm * 2 + 0.1 || longSide / shortSide < 1.5)
            return false;

        var radius = shortSide * 0.5;
        var horizontal = width >= height;
        var cx = (minX + maxX) * 0.5;
        var cy = (minY + maxY) * 0.5;
        var c0 = horizontal ? minX + radius : minY + radius;
        var c1 = horizontal ? maxX - radius : maxY - radius;

        double BoundaryError((double X, double Y) p)
        {
            if (horizontal)
            {
                if (p.X < c0)
                    return Math.Abs(Math.Sqrt((p.X - c0) * (p.X - c0) + (p.Y - cy) * (p.Y - cy)) - radius);
                if (p.X > c1)
                    return Math.Abs(Math.Sqrt((p.X - c1) * (p.X - c1) + (p.Y - cy) * (p.Y - cy)) - radius);
                return Math.Abs(Math.Abs(p.Y - cy) - radius);
            }

            if (p.Y < c0)
                return Math.Abs(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - c0) * (p.Y - c0)) - radius);
            if (p.Y > c1)
                return Math.Abs(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - c1) * (p.Y - c1)) - radius);
            return Math.Abs(Math.Abs(p.X - cx) - radius);
        }

        if (points.Max(BoundaryError) > 0.1)
            return false;

        var rebuilt = LockSlotGeometry.CapsuleFromAabb(
            minX + insetMm,
            maxX - insetMm,
            minY + insetMm,
            maxY - insetMm,
            arcSegments: 4);
        if (rebuilt.Count < 8)
            return false;
        inset = rebuilt
            .Select(p => Rotate((p.X, p.Y), inverse: false))
            .ToList();
        var localCad = LockSlotGeometry.CapsuleSegments(
            minX + insetMm, maxX - insetMm, minY + insetMm, maxY - insetMm);
        cad = Math.Abs(rotationDeg) < 1e-9
            ? localCad
            : CadPath.Rotate(localCad, rotationDeg, new Point2(0, 0));
        return true;
    }
}
