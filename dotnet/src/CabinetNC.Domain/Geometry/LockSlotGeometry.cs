namespace CabinetNC.Domain.Geometry;

/// <summary>
/// Door-lock through slots are stadiums when Fusion tags them as lock cutouts.
/// Geometry size alone is never enough — a sharp 55×15.5 through hole must stay sharp.
/// </summary>
public static class LockSlotGeometry
{
    public const int ArcSegments = 16;

    public static bool IsLockIntent(string? purpose, string? operationType = null)
    {
        var blob = $"{purpose} {operationType}".ToLowerInvariant();
        return blob.Contains("lock", StringComparison.Ordinal);
    }

    /// <summary>
    /// If tagged as a lock (or CAD reported arc edges) and <paramref name="points"/>
    /// is a sharp quad, return a stadium; otherwise return the input unchanged.
    /// Hand-drawn Arc3D stadiums set <paramref name="hasArc"/> even without a lock tag.
    /// </summary>
    public static IReadOnlyList<Point2> EnsureStadium(
        IReadOnlyList<Point2> points,
        string? purpose = null,
        string? operationType = null,
        bool hasArc = false)
    {
        if (points is null || points.Count < 3)
            return points ?? [];
        if (!hasArc && !IsLockIntent(purpose, operationType))
            return points;
        if (!IsSharpQuad(points))
            return points;

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);
        return CapsuleFromAabb(minX, maxX, minY, maxY);
    }

    static bool IsSharpQuad(IReadOnlyList<Point2> points)
    {
        var simplified = SimplifyCollinear(points, 0.2);
        return simplified.Count <= 4;
    }

    static List<Point2> SimplifyCollinear(IReadOnlyList<Point2> points, double tolMm)
    {
        var ring = points.ToList();
        if (ring.Count >= 2)
        {
            var first = ring[0];
            var last = ring[^1];
            if (Math.Abs(first.X - last.X) <= 1e-6 && Math.Abs(first.Y - last.Y) <= 1e-6)
                ring.RemoveAt(ring.Count - 1);
        }
        if (ring.Count < 3) return ring;
        for (var pass = 0; pass < 8; pass++)
        {
            var kept = new List<Point2>(ring.Count);
            var changed = false;
            for (var i = 0; i < ring.Count; i++)
            {
                var prev = ring[(i - 1 + ring.Count) % ring.Count];
                var cur = ring[i];
                var next = ring[(i + 1) % ring.Count];
                var ax = cur.X - prev.X;
                var ay = cur.Y - prev.Y;
                var bx = next.X - cur.X;
                var by = next.Y - cur.Y;
                var cross = Math.Abs(ax * by - ay * bx);
                var scale = Math.Max(1.0, Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)));
                if (cross <= tolMm * scale)
                {
                    changed = true;
                    continue;
                }
                kept.Add(cur);
            }
            if (kept.Count < 3 || !changed || kept.Count == ring.Count)
                break;
            ring = kept;
        }
        return ring;
    }

    public static IReadOnlyList<Point2> CapsuleFromAabb(
        double minX, double maxX, double minY, double maxY, int arcSegments = ArcSegments)
    {
        if (maxX <= minX || maxY <= minY) return [];
        var radius = Math.Min((maxX - minX) * 0.5, (maxY - minY) * 0.5);
        if (radius <= 1e-9) return [];
        var segments = Math.Max(4, arcSegments);
        var horizontal = (maxX - minX) >= (maxY - minY);
        var points = new List<Point2>(segments * 2 + 4);
        if (horizontal)
        {
            var cy = (minY + maxY) * 0.5;
            var leftCx = minX + radius;
            var rightCx = maxX - radius;
            points.Add(new Point2(leftCx, maxY));
            points.Add(new Point2(rightCx, maxY));
            for (var step = 1; step < segments; step++)
            {
                var angle = Math.PI / 2.0 - Math.PI * step / segments;
                points.Add(new Point2(
                    rightCx + radius * Math.Cos(angle),
                    cy + radius * Math.Sin(angle)));
            }
            points.Add(new Point2(rightCx, minY));
            points.Add(new Point2(leftCx, minY));
            for (var step = 1; step < segments; step++)
            {
                var angle = -Math.PI / 2.0 - Math.PI * step / segments;
                points.Add(new Point2(
                    leftCx + radius * Math.Cos(angle),
                    cy + radius * Math.Sin(angle)));
            }
        }
        else
        {
            var cx = (minX + maxX) * 0.5;
            var bottomCy = minY + radius;
            var topCy = maxY - radius;
            points.Add(new Point2(minX, bottomCy));
            points.Add(new Point2(minX, topCy));
            for (var step = 1; step < segments; step++)
            {
                var angle = Math.PI + Math.PI * step / segments;
                points.Add(new Point2(
                    cx + radius * Math.Cos(angle),
                    topCy - radius * Math.Sin(angle)));
            }
            points.Add(new Point2(maxX, topCy));
            points.Add(new Point2(maxX, bottomCy));
            for (var step = 1; step < segments; step++)
            {
                var angle = Math.PI * step / segments;
                points.Add(new Point2(
                    cx + radius * Math.Cos(angle),
                    bottomCy - radius * Math.Sin(angle)));
            }
        }
        return points;
    }

    /// <summary>Analytic stadium (two lines + two 180° caps) for G2/G3 emit.</summary>
    public static IReadOnlyList<CadSegment> CapsuleSegments(
        double minX, double maxX, double minY, double maxY)
    {
        if (maxX <= minX || maxY <= minY) return [];
        var radius = Math.Min((maxX - minX) * 0.5, (maxY - minY) * 0.5);
        if (radius <= 1e-9) return [];
        var horizontal = (maxX - minX) >= (maxY - minY);
        if (horizontal)
        {
            var cy = (minY + maxY) * 0.5;
            var leftCx = minX + radius;
            var rightCx = maxX - radius;
            var topL = new Point2(leftCx, maxY);
            var topR = new Point2(rightCx, maxY);
            var botR = new Point2(rightCx, minY);
            var botL = new Point2(leftCx, minY);
            return
            [
                CadSegment.MakeLine(topL, topR),
                CadSegment.MakeArc(topR, botR, new Point2(rightCx, cy), radius, cw: true),
                CadSegment.MakeLine(botR, botL),
                CadSegment.MakeArc(botL, topL, new Point2(leftCx, cy), radius, cw: true),
            ];
        }

        var cx = (minX + maxX) * 0.5;
        var bottomCy = minY + radius;
        var topCy = maxY - radius;
        var leftB = new Point2(minX, bottomCy);
        var leftT = new Point2(minX, topCy);
        var rightT = new Point2(maxX, topCy);
        var rightB = new Point2(maxX, bottomCy);
        return
        [
            CadSegment.MakeLine(leftB, leftT),
            CadSegment.MakeArc(leftT, rightT, new Point2(cx, topCy), radius, cw: true),
            CadSegment.MakeLine(rightT, rightB),
            CadSegment.MakeArc(rightB, leftB, new Point2(cx, bottomCy), radius, cw: true),
        ];
    }
}
