namespace CabinetNC.Domain.Geometry;

using CabinetNC.Domain.Parts;

/// <summary>
/// Groove display helpers. Machining still uses the centreline path; UI prefers
/// the CAD opening polygon when present, else reconstructs a strip from
/// centreline + width (with a swap guard for axis-flipped exports).
/// </summary>
public static class GrooveGeometry
{
    /// <summary>Closed outline for painting — profile first, then reconstructed strip.</summary>
    public static IReadOnlyList<Point2> DisplayOutline(PanelFeature feature)
    {
        if (!PanelEdit.IsGroove(feature)) return [];
        if (feature.Profile is { Count: >= 3 } profile)
            return profile;
        var width = feature.WidthMm is > 1e-9
            ? feature.WidthMm.Value
            : InferWidthMm(feature.Path, feature.Profile);
        return OutlineFromCenterline(feature.Path, width);
    }

    /// <summary>Prefer explicit width; else short AABB span of path/profile. Never invent 6mm.</summary>
    public static double InferWidthMm(
        IReadOnlyList<Point2>? centerline,
        IReadOnlyList<Point2>? profile = null)
    {
        if (profile is { Count: >= 3 })
        {
            var shortSide = ShortSpan(profile);
            if (shortSide > 1e-9) return shortSide;
        }
        if (centerline is { Count: >= 2 })
        {
            // Without a profile, width is unknown — do not guess board thickness.
            return 0;
        }
        return 0;
    }

    static double ShortSpan(IReadOnlyList<Point2> points)
    {
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return Math.Min(maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Build a closed rectangular strip around <paramref name="centerline"/>.
    /// Returns empty when the path or width is unusable.
    /// </summary>
    public static IReadOnlyList<Point2> OutlineFromCenterline(
        IReadOnlyList<Point2>? centerline,
        double widthMm)
    {
        if (centerline is null || centerline.Count < 2)
            return [];

        if (centerline.Count == 2)
            return OutlineFromSegment(centerline[0], centerline[1], widthMm);

        if (widthMm <= 1e-9) return [];
        var half = widthMm * 0.5;
        var left = new List<Point2>(centerline.Count);
        var right = new List<Point2>(centerline.Count);
        for (var i = 0; i < centerline.Count; i++)
        {
            var tangent = TangentAt(centerline, i);
            var len = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
            if (len <= 1e-12) continue;
            var nx = -tangent.Y / len;
            var ny = tangent.X / len;
            var p = centerline[i];
            left.Add(new Point2(p.X + nx * half, p.Y + ny * half));
            right.Add(new Point2(p.X - nx * half, p.Y - ny * half));
        }
        if (left.Count < 2 || right.Count < 2) return [];

        var outline = new List<Point2>(left.Count + right.Count);
        outline.AddRange(left);
        for (var i = right.Count - 1; i >= 0; i--)
            outline.Add(right[i]);
        return outline;
    }

    static IReadOnlyList<Point2> OutlineFromSegment(Point2 a, Point2 b, double widthMm)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var clLen = Math.Sqrt(dx * dx + dy * dy);
        if (clLen <= 1e-12) return [];

        // Axis-swap guard: some Fusion exports put length into widthMm and a
        // short across-width segment into the centreline.
        var width = widthMm;
        Point2 start = a;
        Point2 finish = b;
        if (width > clLen * 1.5)
        {
            var midX = (a.X + b.X) * 0.5;
            var midY = (a.Y + b.Y) * 0.5;
            var ux = dx / clLen;
            var uy = dy / clLen;
            var px = -uy;
            var py = ux;
            var halfLen = width * 0.5;
            start = new Point2(midX - px * halfLen, midY - py * halfLen);
            finish = new Point2(midX + px * halfLen, midY + py * halfLen);
            width = clLen;
        }
        else if (width <= 1e-9)
        {
            return [];
        }

        var sdx = finish.X - start.X;
        var sdy = finish.Y - start.Y;
        var len = Math.Sqrt(sdx * sdx + sdy * sdy);
        if (len <= 1e-12) return [];
        var half = width * 0.5;
        var nx = -sdy / len;
        var ny = sdx / len;
        return
        [
            new Point2(start.X + nx * half, start.Y + ny * half),
            new Point2(finish.X + nx * half, finish.Y + ny * half),
            new Point2(finish.X - nx * half, finish.Y - ny * half),
            new Point2(start.X - nx * half, start.Y - ny * half),
        ];
    }

    static Point2 TangentAt(IReadOnlyList<Point2> path, int index)
    {
        if (index <= 0)
            return new Point2(path[1].X - path[0].X, path[1].Y - path[0].Y);
        if (index >= path.Count - 1)
        {
            var a = path[^2];
            var b = path[^1];
            return new Point2(b.X - a.X, b.Y - a.Y);
        }
        var prev = path[index - 1];
        var next = path[index + 1];
        return new Point2(next.X - prev.X, next.Y - prev.Y);
    }
}
