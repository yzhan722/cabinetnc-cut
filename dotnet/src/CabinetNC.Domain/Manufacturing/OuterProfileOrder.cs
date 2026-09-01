namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;

/// <summary>
/// Rank-4 outer-profile sequence. First pass: shortest air travel.
/// Last pass: children before hosts, edge-in, skip already-cut AABBs.
/// </summary>
public static class OuterProfileOrder
{
    public const double MinStartEdgeMm = 20;

    public static IReadOnlyList<CutOp> Fastest(
        IReadOnlyList<CutOp> ops, double fromX, double fromY)
    {
        var list = ops.Where(o => o.Path is { Count: >= 3 }).ToList();
        if (list.Count <= 1) return list;

        var left = list.ToList();
        var tour = new List<CutOp>(list.Count);
        var x = fromX;
        var y = fromY;
        while (left.Count > 0)
        {
            var bestI = 0;
            var bestD = double.PositiveInfinity;
            for (var i = 0; i < left.Count; i++)
            {
                var d = EntryDistance(left[i].Path!, x, y);
                if (d < bestD - 1e-9
                    || (Math.Abs(d - bestD) < 1e-9
                        && string.CompareOrdinal(left[i].PanelId, left[bestI].PanelId) < 0))
                {
                    bestD = d;
                    bestI = i;
                }
            }

            var pick = left[bestI];
            left.RemoveAt(bestI);
            tour.Add(pick);
            var hit = EntryPoint(pick.Path!, x, y);
            x = hit.X;
            y = hit.Y;
        }

        return TwoOpt(tour, fromX, fromY);
    }

    public static IReadOnlyList<CutOp> Safest(
        IReadOnlyList<CutOp> ops, double fromX, double fromY)
    {
        var list = ops.Where(o => o.Path is { Count: >= 3 }).ToList();
        if (list.Count <= 1) return list;

        var childrenOf = Containment(list);
        var cx = list.Average(o => Centroid(o.Path!).X);
        var cy = list.Average(o => Centroid(o.Path!).Y);

        var left = list.ToList();
        var done = new List<CutOp>(list.Count);
        var cutBoxes = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        var x = fromX;
        var y = fromY;

        while (left.Count > 0)
        {
            var ready = left.Where(o =>
                !childrenOf.TryGetValue(o.PanelId, out var kids)
                || kids.All(k => done.Any(d => d.PanelId == k))).ToList();
            if (ready.Count == 0)
                ready = left.ToList();

            CutOp? pick = null;
            var bestCross = int.MaxValue;
            var bestOut = double.NegativeInfinity;
            var bestArea = double.PositiveInfinity;
            var bestD = double.PositiveInfinity;
            foreach (var o in ready)
            {
                var start = EntryPoint(o.Path!, x, y);
                var cross = CrossesCut(x, y, start.X, start.Y, cutBoxes) ? 1 : 0;
                var outside = Dist2(start.X, start.Y, cx, cy);
                var area = Math.Abs(ClimbCut.SignedArea(o.Path!));
                var d = EntryDistance(o.Path!, x, y);
                var better = cross < bestCross
                    || (cross == bestCross && outside > bestOut + 1e-6)
                    || (cross == bestCross && Math.Abs(outside - bestOut) <= 1e-6 && area < bestArea - 1e-3)
                    || (cross == bestCross && Math.Abs(outside - bestOut) <= 1e-6
                        && Math.Abs(area - bestArea) <= 1e-3 && d < bestD - 1e-9);
                if (!better) continue;
                pick = o;
                bestCross = cross;
                bestOut = outside;
                bestArea = area;
                bestD = d;
            }

            pick ??= ready[0];
            left.Remove(pick);
            done.Add(pick);
            var end = EntryPoint(pick.Path!, x, y);
            cutBoxes.Add(Aabb(pick.Path!));
            x = end.X;
            y = end.Y;
        }

        return done;
    }

    public static double EntryArc(IReadOnlyList<(double X, double Y)> path, double fromX, double fromY)
    {
        var i = EntryVertex(path, fromX, fromY);
        return VertexArc(path, i);
    }

    public static (double X, double Y) EntryPoint(
        IReadOnlyList<(double X, double Y)> path, double fromX, double fromY)
    {
        var i = EntryVertex(path, fromX, fromY);
        return path[i];
    }

    public static double EntryDistance(
        IReadOnlyList<(double X, double Y)> path, double fromX, double fromY)
    {
        var p = EntryPoint(path, fromX, fromY);
        return Math.Sqrt(Dist2(fromX, fromY, p.X, p.Y));
    }

    static int EntryVertex(IReadOnlyList<(double X, double Y)> path, double fromX, double fromY)
    {
        if (path.Count < 2) return 0;
        var n = path.Count;
        var lens = new double[n];
        var longest = 0d;
        for (var i = 0; i < n; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % n];
            lens[i] = Math.Sqrt(Dist2(a.X, a.Y, b.X, b.Y));
            if (lens[i] > longest) longest = lens[i];
        }

        var min = Math.Min(MinStartEdgeMm, longest);
        var bestI = 0;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < n; i++)
        {
            if (lens[i] + 1e-9 < min) continue;
            var d = Dist2(fromX, fromY, path[i].X, path[i].Y);
            if (d < bestD)
            {
                bestD = d;
                bestI = i;
            }
        }

        return bestI;
    }

    static double VertexArc(IReadOnlyList<(double X, double Y)> path, int vertex)
    {
        if (vertex <= 0) return 0;
        double arc = 0;
        for (var i = 0; i < vertex; i++)
        {
            var a = path[i];
            var b = path[i + 1];
            arc += Math.Sqrt(Dist2(a.X, a.Y, b.X, b.Y));
        }

        return arc;
    }

    static IReadOnlyList<CutOp> TwoOpt(List<CutOp> tour, double fromX, double fromY)
    {
        if (tour.Count < 3) return tour;
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var i = 0; i < tour.Count - 1; i++)
            {
                for (var k = i + 1; k < tour.Count; k++)
                {
                    var cur = TourTravel(tour, fromX, fromY);
                    tour.Reverse(i, k - i + 1);
                    var next = TourTravel(tour, fromX, fromY);
                    if (next + 1e-6 < cur)
                        improved = true;
                    else
                        tour.Reverse(i, k - i + 1);
                }
            }
        }

        return tour;
    }

    static double TourTravel(IReadOnlyList<CutOp> tour, double fromX, double fromY)
    {
        var x = fromX;
        var y = fromY;
        var sum = 0d;
        foreach (var o in tour)
        {
            sum += EntryDistance(o.Path!, x, y);
            var p = EntryPoint(o.Path!, x, y);
            x = p.X;
            y = p.Y;
        }

        return sum;
    }

    /// <summary>Host panel id → child panel ids whose centroid sits inside the host.</summary>
    static Dictionary<string, List<string>> Containment(IReadOnlyList<CutOp> ops)
    {
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < ops.Count; i++)
        {
            var child = ops[i];
            var c = Centroid(child.Path!);
            var childArea = Math.Abs(ClimbCut.SignedArea(child.Path!));
            for (var j = 0; j < ops.Count; j++)
            {
                if (i == j) continue;
                var host = ops[j];
                var hostArea = Math.Abs(ClimbCut.SignedArea(host.Path!));
                if (hostArea < childArea + 1e-3) continue;
                if (!PointInRing(c.X, c.Y, host.Path!)) continue;
                if (!children.TryGetValue(host.PanelId, out var list))
                {
                    list = [];
                    children[host.PanelId] = list;
                }

                if (!list.Contains(child.PanelId))
                    list.Add(child.PanelId);
            }
        }

        return children;
    }

    static bool PointInRing(double x, double y, IReadOnlyList<(double X, double Y)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var yi = ring[i].Y;
            var yj = ring[j].Y;
            var xi = ring[i].X;
            var xj = ring[j].X;
            if ((yi > y) == (yj > y)) continue;
            var xInt = (xj - xi) * (y - yi) / (yj - yi + 1e-18) + xi;
            if (x < xInt) inside = !inside;
        }

        return inside;
    }

    static (double X, double Y) Centroid(IReadOnlyList<(double X, double Y)> path)
    {
        double x = 0, y = 0;
        foreach (var p in path)
        {
            x += p.X;
            y += p.Y;
        }

        return (x / path.Count, y / path.Count);
    }

    static (double MinX, double MinY, double MaxX, double MaxY) Aabb(
        IReadOnlyList<(double X, double Y)> path)
    {
        var minX = path.Min(p => p.X);
        var minY = path.Min(p => p.Y);
        var maxX = path.Max(p => p.X);
        var maxY = path.Max(p => p.Y);
        return (minX, minY, maxX, maxY);
    }

    static bool CrossesCut(
        double x0, double y0, double x1, double y1,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> boxes)
    {
        foreach (var b in boxes)
        {
            if (SegHitsAabb(x0, y0, x1, y1, b.MinX, b.MinY, b.MaxX, b.MaxY))
                return true;
        }

        return false;
    }

    static bool SegHitsAabb(
        double x0, double y0, double x1, double y1,
        double minX, double minY, double maxX, double maxY)
    {
        var pad = 2;
        minX += pad;
        minY += pad;
        maxX -= pad;
        maxY -= pad;
        if (maxX <= minX || maxY <= minY) return false;
        if (x0 >= minX && x0 <= maxX && y0 >= minY && y0 <= maxY) return true;
        if (x1 >= minX && x1 <= maxX && y1 >= minY && y1 <= maxY) return true;

        return Hits(x0, y0, x1, y1, minX, minY, maxX, minY)
            || Hits(x0, y0, x1, y1, maxX, minY, maxX, maxY)
            || Hits(x0, y0, x1, y1, maxX, maxY, minX, maxY)
            || Hits(x0, y0, x1, y1, minX, maxY, minX, minY);
    }

    static bool Hits(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        var d = (bx - ax) * (dy - cy) - (by - ay) * (dx - cx);
        if (Math.Abs(d) < 1e-12) return false;
        var t = ((cx - ax) * (dy - cy) - (cy - ay) * (dx - cx)) / d;
        var u = ((cx - ax) * (by - ay) - (cy - ay) * (bx - ax)) / d;
        return t is > 0 and < 1 && u is > 0 and < 1;
    }

    static double Dist2(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }
}
