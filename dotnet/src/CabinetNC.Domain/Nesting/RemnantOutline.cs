using CabinetNC.Domain.Geometry;

namespace CabinetNC.Domain.Nesting;

/// <summary>
/// Shop remnant outline: a simple axis-aligned closed ring.
/// The bounding box becomes the nest sheet; missing rectangles inside that
/// box are <see cref="NestBlockedRect"/> keep-outs so an L / U still packs
/// as free rectangles.
/// </summary>
public sealed class RemnantShape
{
    public required IReadOnlyList<Point2> Outline { get; init; }
    public double WidthMm { get; init; }
    public double LengthMm { get; init; }
    public IReadOnlyList<NestBlockedRect> Blocked { get; init; } = [];

    public bool IsRectangle => Blocked.Count == 0;
}

public static class RemnantOutline
{
    public const double TolMm = 0.05;

    public static bool TryBuild(
        IReadOnlyList<Point2> points,
        out RemnantShape? shape,
        out string? error)
    {
        shape = null;
        var ring = UniqueRing(points);
        if (ring.Count < 4)
        {
            error = "余料外框至少 4 个角（直角多边形）";
            return false;
        }

        if (!IsAxisAligned(ring))
        {
            error = "余料外框必须是直角边（水平 / 竖直）";
            return false;
        }

        ring = CollapseColinear(ring);
        if (ring.Count < 4)
        {
            error = "余料外框至少 4 个角（直角多边形）";
            return false;
        }

        var area = SignedArea(ring);
        if (Math.Abs(area) < 1)
        {
            error = "余料面积太小";
            return false;
        }

        if (area < 0)
            ring.Reverse();

        if (SelfIntersects(ring))
        {
            error = "余料外框不能自交";
            return false;
        }

        var minX = ring.Min(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxX = ring.Max(p => p.X);
        var maxY = ring.Max(p => p.Y);
        var w = maxX - minX;
        var h = maxY - minY;
        if (w < 1 || h < 1)
        {
            error = "余料宽/长须 > 0";
            return false;
        }

        var shifted = ring.Select(p => new Point2(Snap(p.X - minX), Snap(p.Y - minY))).ToList();
        var blocked = BlockedInBBox(shifted, w, h);
        shape = new RemnantShape
        {
            Outline = shifted,
            WidthMm = w,
            LengthMm = h,
            Blocked = blocked,
        };
        error = null;
        return true;
    }

    public static IReadOnlyList<Point2> Rectangle(double widthMm, double lengthMm) =>
    [
        new(0, 0),
        new(widthMm, 0),
        new(widthMm, lengthMm),
        new(0, lengthMm),
    ];

    static List<Point2> UniqueRing(IReadOnlyList<Point2> pts)
    {
        var list = new List<Point2>(pts.Count);
        foreach (var p in pts)
        {
            if (list.Count > 0 && Near(list[^1], p)) continue;
            list.Add(p);
        }

        if (list.Count >= 2 && Near(list[0], list[^1]))
            list.RemoveAt(list.Count - 1);
        return list;
    }

    static List<Point2> CollapseColinear(List<Point2> ring)
    {
        if (ring.Count < 3) return ring;
        var keep = new List<Point2>(ring.Count);
        for (var i = 0; i < ring.Count; i++)
        {
            var prev = ring[(i - 1 + ring.Count) % ring.Count];
            var cur = ring[i];
            var next = ring[(i + 1) % ring.Count];
            var colinear =
                (Math.Abs(prev.X - cur.X) <= TolMm && Math.Abs(cur.X - next.X) <= TolMm)
                || (Math.Abs(prev.Y - cur.Y) <= TolMm && Math.Abs(cur.Y - next.Y) <= TolMm);
            if (!colinear)
                keep.Add(cur);
        }

        return keep.Count >= 3 ? keep : ring;
    }

    static bool IsAxisAligned(IReadOnlyList<Point2> ring)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            var horiz = dy <= TolMm && dx > TolMm;
            var vert = dx <= TolMm && dy > TolMm;
            if (!horiz && !vert)
                return false;
        }

        return true;
    }

    static bool SelfIntersects(IReadOnlyList<Point2> ring)
    {
        var n = ring.Count;
        for (var i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                if (Math.Abs(i - j) <= 1 || (i == 0 && j == n - 1))
                    continue;
                var c = ring[j];
                var d = ring[(j + 1) % n];
                if (SegmentsInterfere(a, b, c, d))
                    return true;
            }
        }

        return false;
    }

    static bool SegmentsInterfere(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        if (Near(a, c) || Near(a, d) || Near(b, c) || Near(b, d))
            return false;

        var abH = Math.Abs(a.Y - b.Y) <= TolMm;
        var cdH = Math.Abs(c.Y - d.Y) <= TolMm;
        if (abH == cdH)
        {
            if (abH)
            {
                if (Math.Abs(a.Y - c.Y) > TolMm) return false;
                var a0 = Math.Min(a.X, b.X);
                var a1 = Math.Max(a.X, b.X);
                var c0 = Math.Min(c.X, d.X);
                var c1 = Math.Max(c.X, d.X);
                return a0 < c1 - TolMm && c0 < a1 - TolMm;
            }

            if (Math.Abs(a.X - c.X) > TolMm) return false;
            var ay0 = Math.Min(a.Y, b.Y);
            var ay1 = Math.Max(a.Y, b.Y);
            var cy0 = Math.Min(c.Y, d.Y);
            var cy1 = Math.Max(c.Y, d.Y);
            return ay0 < cy1 - TolMm && cy0 < ay1 - TolMm;
        }

        Point2 h0, h1, v0, v1;
        if (abH) { h0 = a; h1 = b; v0 = c; v1 = d; }
        else { h0 = c; h1 = d; v0 = a; v1 = b; }

        var y = h0.Y;
        var x = v0.X;
        var xmin = Math.Min(h0.X, h1.X);
        var xmax = Math.Max(h0.X, h1.X);
        var ymin = Math.Min(v0.Y, v1.Y);
        var ymax = Math.Max(v0.Y, v1.Y);
        var onH = x >= xmin - TolMm && x <= xmax + TolMm;
        var onV = y >= ymin - TolMm && y <= ymax + TolMm;
        if (!onH || !onV) return false;
        var atHEnd = Math.Abs(x - h0.X) <= TolMm || Math.Abs(x - h1.X) <= TolMm;
        var atVEnd = Math.Abs(y - v0.Y) <= TolMm || Math.Abs(y - v1.Y) <= TolMm;
        return !(atHEnd && atVEnd);
    }

    static List<NestBlockedRect> BlockedInBBox(IReadOnlyList<Point2> ring, double w, double h)
    {
        var xs = UniqueSorted(ring.Select(p => p.X).Concat([0, w]));
        var ys = UniqueSorted(ring.Select(p => p.Y).Concat([0, h]));
        if (xs.Count < 2 || ys.Count < 2) return [];

        var cells = new List<NestBlockedRect>();
        for (var i = 0; i < xs.Count - 1; i++)
        for (var j = 0; j < ys.Count - 1; j++)
        {
            var x0 = xs[i];
            var x1 = xs[i + 1];
            var y0 = ys[j];
            var y1 = ys[j + 1];
            if (x1 - x0 < 0.5 || y1 - y0 < 0.5) continue;
            var midX = (x0 + x1) * 0.5;
            var midY = (y0 + y1) * 0.5;
            if (!Contains(ring, midX, midY))
                cells.Add(new NestBlockedRect { MinX = x0, MinY = y0, MaxX = x1, MaxY = y1 });
        }

        return MergeRects(cells);
    }

    static List<double> UniqueSorted(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        var r = new List<double>();
        foreach (var v in list)
        {
            if (r.Count == 0 || Math.Abs(r[^1] - v) > TolMm)
                r.Add(v);
        }

        return r;
    }

    static List<NestBlockedRect> MergeRects(List<NestBlockedRect> cells)
    {
        if (cells.Count <= 1) return cells;
        var rows = new List<NestBlockedRect>();
        foreach (var g in cells.GroupBy(c => (c.MinY, c.MaxY)).OrderBy(g => g.Key.MinY))
        {
            var xs = g.OrderBy(c => c.MinX).ToList();
            var cur = xs[0];
            for (var i = 1; i < xs.Count; i++)
            {
                if (Math.Abs(xs[i].MinX - cur.MaxX) <= TolMm)
                {
                    cur = new NestBlockedRect
                    {
                        MinX = cur.MinX,
                        MinY = cur.MinY,
                        MaxX = xs[i].MaxX,
                        MaxY = cur.MaxY,
                    };
                }
                else
                {
                    rows.Add(cur);
                    cur = xs[i];
                }
            }

            rows.Add(cur);
        }

        rows.Sort((a, b) =>
        {
            var cx = a.MinX.CompareTo(b.MinX);
            return cx != 0 ? cx : a.MinY.CompareTo(b.MinY);
        });
        var result = new List<NestBlockedRect>();
        foreach (var r in rows)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                if (Math.Abs(last.MinX - r.MinX) <= TolMm
                    && Math.Abs(last.MaxX - r.MaxX) <= TolMm
                    && Math.Abs(last.MaxY - r.MinY) <= TolMm)
                {
                    result[^1] = new NestBlockedRect
                    {
                        MinX = last.MinX,
                        MinY = last.MinY,
                        MaxX = last.MaxX,
                        MaxY = r.MaxY,
                    };
                    continue;
                }
            }

            result.Add(r);
        }

        return result;
    }

    static bool Contains(IReadOnlyList<Point2> ring, double x, double y)
    {
        var inside = false;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if ((a.Y > y) == (b.Y > y)) continue;
            var dy = b.Y - a.Y;
            if (Math.Abs(dy) < 1e-18) continue;
            var xint = (b.X - a.X) * (y - a.Y) / dy + a.X;
            if (x < xint) inside = !inside;
        }

        return inside;
    }

    static double SignedArea(IReadOnlyList<Point2> pts)
    {
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum * 0.5;
    }

    static bool Near(Point2 a, Point2 b) =>
        Math.Abs(a.X - b.X) <= TolMm && Math.Abs(a.Y - b.Y) <= TolMm;

    static double Snap(double v) => Math.Abs(v) < 1e-9 ? 0 : v;
}
