namespace CabinetNC.Domain.Geometry;

/// <summary>
/// Closed CAD loops: transform, sample, analytic offset, G1/G2 emit.
/// </summary>
public static class CadPath
{
    const double Tol = 1e-6;

    public static IReadOnlyList<CadSegment> Translate(
        IReadOnlyList<CadSegment>? segs, double dx, double dy) =>
        Map(segs, p => new Point2(p.X + dx, p.Y + dy));

    public static IReadOnlyList<CadSegment> Rotate(
        IReadOnlyList<CadSegment>? segs, double degrees, Point2 origin)
    {
        if (segs is not { Count: > 0 } || Math.Abs(degrees) < 1e-12)
            return segs ?? [];
        var r = degrees * Math.PI / 180;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return Map(segs, p =>
        {
            var x = p.X - origin.X;
            var y = p.Y - origin.Y;
            return new Point2(origin.X + x * c - y * s, origin.Y + x * s + y * c);
        });
    }

    public static IReadOnlyList<CadSegment> Map(
        IReadOnlyList<CadSegment>? segs,
        Func<Point2, Point2> map,
        bool flipCw = false)
    {
        if (segs is not { Count: > 0 }) return [];
        var list = new List<CadSegment>(segs.Count);
        foreach (var g in segs)
        {
            var start = map(g.Start);
            var end = map(g.End);
            Point2? center = g.Center is { } c ? map(c) : null;
            list.Add(g with
            {
                Start = start,
                End = end,
                Center = center,
                Cw = flipCw ? !g.Cw : g.Cw,
            });
        }
        return list;
    }

    public static IReadOnlyList<CadSegment> Reverse(IReadOnlyList<CadSegment> segs)
    {
        var list = new List<CadSegment>(segs.Count);
        for (var i = segs.Count - 1; i >= 0; i--)
        {
            var g = segs[i];
            list.Add(g with
            {
                Start = g.End,
                End = g.Start,
                Cw = g.IsArc ? !g.Cw : g.Cw,
            });
        }
        return list;
    }

    public static double Length(IReadOnlyList<CadSegment> segs, bool closed = true)
    {
        var total = 0d;
        foreach (var g in segs)
            total += LengthOf(g);
        return total;
    }

    public static double LengthOf(CadSegment g)
    {
        if (g.IsCircle && g.RadiusMm > 0)
            return 2 * Math.PI * g.RadiusMm;
        if (g.IsArc && g.Center is { } c && g.RadiusMm > 0)
            return g.RadiusMm * SweepRad(c, g.Start, g.End, g.Cw);
        return Hyp(g.End.X - g.Start.X, g.End.Y - g.Start.Y);
    }

    public static Point2 PointAt(IReadOnlyList<CadSegment> segs, double arc, bool closed = true)
    {
        var total = Length(segs, closed);
        if (total < Tol) return segs.Count > 0 ? segs[0].Start : default;
        if (closed)
        {
            arc %= total;
            if (arc < 0) arc += total;
        }
        else
            arc = Math.Clamp(arc, 0, total);

        var walked = 0d;
        foreach (var g in segs)
        {
            var len = LengthOf(g);
            if (walked + len >= arc - 1e-9)
                return PointOn(g, len < Tol ? 0 : (arc - walked) / len);
            walked += len;
        }
        return segs[^1].End;
    }

    public static IReadOnlyList<(double X, double Y)> ToPolyline(
        IReadOnlyList<CadSegment> segs, double maxChordMm = 0.4)
    {
        var pts = new List<(double X, double Y)>();
        void Add(Point2 p)
        {
            if (pts.Count == 0
                || Math.Abs(pts[^1].X - p.X) > 1e-6
                || Math.Abs(pts[^1].Y - p.Y) > 1e-6)
                pts.Add((p.X, p.Y));
        }

        foreach (var g in segs)
        {
            Add(g.Start);
            if (g.IsArc && g.Center is { } c && g.RadiusMm > 0)
            {
                var sweep = g.IsCircle ? 2 * Math.PI : SweepRad(c, g.Start, g.End, g.Cw);
                var steps = Math.Max(2, (int)Math.Ceiling(
                    Math.Abs(sweep) * g.RadiusMm / Math.Max(0.1, maxChordMm)));
                for (var i = 1; i < steps; i++)
                    Add(PointOn(g, i / (double)steps));
            }
            Add(g.End);
        }
        return pts;
    }

    public static IReadOnlyList<CadSegment> Slice(
        IReadOnlyList<CadSegment> segs, double a0, double a1, bool closed)
    {
        var total = Length(segs, closed);
        if (total < Tol || segs.Count == 0) return [];
        a0 = closed ? Wrap(a0, total) : Math.Clamp(a0, 0, total);
        a1 = closed ? Wrap(a1, total) : Math.Clamp(a1, 0, total);
        if (Math.Abs(a1 - a0) < 1e-9)
            return [];
        if (closed && a1 < a0 - 1e-9)
        {
            var a = SliceLinear(segs, a0, total, total);
            var b = SliceLinear(segs, 0, a1, total);
            return a.Concat(b).ToList();
        }
        return SliceLinear(segs, Math.Min(a0, a1), Math.Max(a0, a1), total);
    }

    static List<CadSegment> SliceLinear(
        IReadOnlyList<CadSegment> segs, double a0, double a1, double total)
    {
        var outSegs = new List<CadSegment>();
        var walked = 0d;
        foreach (var g in segs)
        {
            var len = LengthOf(g);
            var b0 = walked;
            var b1 = walked + len;
            walked = b1;
            if (b1 < a0 - 1e-9 || b0 > a1 + 1e-9) continue;
            var t0 = len < Tol ? 0 : Math.Clamp((a0 - b0) / len, 0, 1);
            var t1 = len < Tol ? 1 : Math.Clamp((a1 - b0) / len, 0, 1);
            if (t1 - t0 < 1e-9) continue;
            outSegs.Add(Sub(g, t0, t1));
        }
        return outSegs;
    }

    static CadSegment Sub(CadSegment g, double t0, double t1)
    {
        var a = PointOn(g, t0);
        var b = PointOn(g, t1);
        return g with { Start = a, End = b };
    }

    static Point2 PointOn(CadSegment g, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (g.IsArc && g.Center is { } c && g.RadiusMm > 0)
        {
            var a0 = Math.Atan2(g.Start.Y - c.Y, g.Start.X - c.X);
            var sweep = g.IsCircle ? 2 * Math.PI * (g.Cw ? -1 : 1) : SignedSweep(c, g.Start, g.End, g.Cw);
            var a = a0 + sweep * t;
            return new Point2(c.X + g.RadiusMm * Math.Cos(a), c.Y + g.RadiusMm * Math.Sin(a));
        }
        return new Point2(
            g.Start.X + (g.End.X - g.Start.X) * t,
            g.Start.Y + (g.End.Y - g.Start.Y) * t);
    }

    public static double SweepRad(Point2 c, Point2 start, Point2 end, bool cw)
    {
        return Math.Abs(SignedSweep(c, start, end, cw));
    }

    static double SignedSweep(Point2 c, Point2 start, Point2 end, bool cw)
    {
        var a0 = Math.Atan2(start.Y - c.Y, start.X - c.X);
        var a1 = Math.Atan2(end.Y - c.Y, end.X - c.X);
        var d = a1 - a0;
        if (cw)
        {
            while (d > 1e-12) d -= 2 * Math.PI;
            if (Math.Abs(d) < 1e-12) d = -2 * Math.PI;
        }
        else
        {
            while (d < -1e-12) d += 2 * Math.PI;
            if (Math.Abs(d) < 1e-12) d = 2 * Math.PI;
        }
        return d;
    }

    public static double SignedArea(IReadOnlyList<CadSegment> segs)
    {
        var pts = ToPolyline(segs, 2);
        if (pts.Count < 3) return 0;
        double a = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a * 0.5;
    }

    public static IReadOnlyList<CadSegment> OrientClosed(
        IReadOnlyList<CadSegment> segs, bool inner)
    {
        if (segs.Count == 0) return segs;
        var ccw = SignedArea(segs) > 0;
        var wantCcw = inner;
        var oriented = ccw == wantCcw ? segs : Reverse(segs);
        return StartAtLongest(oriented);
    }

    public static IReadOnlyList<CadSegment> StartAtLongest(IReadOnlyList<CadSegment> segs)
    {
        if (segs.Count < 2) return segs;
        var bestI = 0;
        var bestL = -1d;
        for (var i = 0; i < segs.Count; i++)
        {
            var l = LengthOf(segs[i]);
            if (l > bestL)
            {
                bestL = l;
                bestI = i;
            }
        }
        if (bestI == 0) return segs is List<CadSegment> list ? list : segs.ToList();
        var rot = new List<CadSegment>(segs.Count);
        for (var k = 0; k < segs.Count; k++)
            rot.Add(segs[(bestI + k) % segs.Count]);
        return rot;
    }

    /// <summary>
    /// Offset a closed CAD loop. <paramref name="signedOffset"/> matches Clipper:
    /// positive expands the polygon, negative shrinks. Convex corners get a
    /// tool-radius arc when <paramref name="roundConvex"/> is true (outer profile).
    /// Notch / concave corners stay line-line.
    /// </summary>
    public static bool TryOffset(
        IReadOnlyList<CadSegment> source,
        double signedOffset,
        bool roundConvex,
        out IReadOnlyList<CadSegment> result)
    {
        result = [];
        if (source.Count == 0 || Math.Abs(signedOffset) < Tol)
        {
            result = source;
            return source.Count > 0;
        }

        if (source.Count == 1 && source[0].IsCircle && source[0].Center is { } circ)
        {
            var newR = source[0].RadiusMm + signedOffset;
            if (newR <= 0.05) return false;
            var start = Radial(circ, source[0].Start, newR);
            result = [source[0] with { Start = start, End = start, RadiusMm = newR }];
            return true;
        }

        var ccw = SignedArea(source) > 0;
        var expand = signedOffset;
        var leftOffset = (ccw ? -1.0 : 1.0) * expand;
        var raw = new List<CadSegment>(source.Count);
        var verts = new List<Point2>(source.Count);
        foreach (var g in source)
        {
            verts.Add(g.Start);
            if (!TryOffsetOne(g, leftOffset, out var off))
                return false;
            raw.Add(off);
        }

        var n = raw.Count;
        var starts = raw.Select(g => g.Start).ToArray();
        var ends = raw.Select(g => g.End).ToArray();
        var extras = new CadSegment?[n];
        var toLeft = leftOffset > 0;
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var cur = raw[i] with { Start = starts[i], End = ends[i] };
            var next = raw[j] with { Start = starts[j], End = ends[j] };
            if (NeedSkipJoin(cur.End, next.Start))
                continue;

            var v = source[j].Start;
            var inDir = TangentAtEnd(source[i]);
            var outDir = TangentAtStart(source[j]);
            var cross = inDir.X * outDir.Y - inDir.Y * outDir.X;
            var convex = (ccw && cross > 1e-9) || (!ccw && cross < -1e-9);
            var needRound = roundConvex && convex && Math.Abs(expand) > 0.05;

            if (needRound)
            {
                var radius = Math.Abs(expand);
                var start = Radial(v, cur.End, radius);
                var end = Radial(v, next.Start, radius);
                ends[i] = start;
                starts[j] = end;
                extras[i] = CadSegment.MakeArc(start, end, v, radius, toLeft);
                continue;
            }

            if (TryIntersect(cur, next, out var hit))
            {
                ends[i] = hit;
                starts[j] = hit;
            }
        }

        var joined = new List<CadSegment>(n * 2);
        for (var i = 0; i < n; i++)
        {
            joined.Add(raw[i] with { Start = starts[i], End = ends[i] });
            if (extras[i] is { } extra)
                joined.Add(extra);
        }

        if (joined.Count < 1) return false;
        result = joined;
        return true;
    }

    static bool TryOffsetOne(CadSegment g, double leftOffset, out CadSegment off)
    {
        off = g;
        if (g.IsCircle && g.Center is { } cc)
        {
            var newR = g.RadiusMm + (g.Cw ? leftOffset : -leftOffset);
            if (newR <= 0.05) return false;
            var start = Radial(cc, g.Start, newR);
            off = g with { Start = start, End = start, RadiusMm = newR };
            return true;
        }
        if (g.IsArc && g.Center is { } c && g.RadiusMm > 0)
        {
            var newR = g.RadiusMm + (g.Cw ? leftOffset : -leftOffset);
            if (newR <= 0.05) return false;
            off = g with
            {
                Start = Radial(c, g.Start, newR),
                End = Radial(c, g.End, newR),
                RadiusMm = newR,
            };
            return true;
        }
        var dx = g.End.X - g.Start.X;
        var dy = g.End.Y - g.Start.Y;
        var len = Hyp(dx, dy);
        if (len < Tol) return false;
        var nx = -dy / len * leftOffset;
        var ny = dx / len * leftOffset;
        off = CadSegment.MakeLine(
            new Point2(g.Start.X + nx, g.Start.Y + ny),
            new Point2(g.End.X + nx, g.End.Y + ny));
        return true;
    }

    static (double X, double Y) TangentAtStart(CadSegment g)
    {
        if (g.IsArc && g.Center is { } c)
        {
            var tx = g.Start.X - c.X;
            var ty = g.Start.Y - c.Y;
            return g.Cw ? (ty, -tx) : (-ty, tx);
        }
        return (g.End.X - g.Start.X, g.End.Y - g.Start.Y);
    }

    static (double X, double Y) TangentAtEnd(CadSegment g)
    {
        if (g.IsArc && g.Center is { } c)
        {
            var tx = g.End.X - c.X;
            var ty = g.End.Y - c.Y;
            return g.Cw ? (ty, -tx) : (-ty, tx);
        }
        return (g.End.X - g.Start.X, g.End.Y - g.Start.Y);
    }

    static bool NeedSkipJoin(Point2 a, Point2 b) =>
        Hyp(a.X - b.X, a.Y - b.Y) < 0.05;

    static bool TryIntersect(CadSegment a, CadSegment b, out Point2 hit)
    {
        hit = default;
        if (a.IsLine && b.IsLine)
            return LineLine(a.Start, a.End, b.Start, b.End, out hit);
        return false;
    }

    static bool LineLine(Point2 p1, Point2 p2, Point2 q1, Point2 q2, out Point2 hit)
    {
        var rx = p2.X - p1.X;
        var ry = p2.Y - p1.Y;
        var sx = q2.X - q1.X;
        var sy = q2.Y - q1.Y;
        var den = rx * sy - ry * sx;
        hit = default;
        if (Math.Abs(den) < 1e-12) return false;
        var t = ((q1.X - p1.X) * sy - (q1.Y - p1.Y) * sx) / den;
        hit = new Point2(p1.X + t * rx, p1.Y + t * ry);
        return double.IsFinite(hit.X) && double.IsFinite(hit.Y);
    }

    static Point2 Radial(Point2 center, Point2 from, double radius)
    {
        var dx = from.X - center.X;
        var dy = from.Y - center.Y;
        var len = Hyp(dx, dy);
        if (len < Tol) return new Point2(center.X + radius, center.Y);
        return new Point2(center.X + dx / len * radius, center.Y + dy / len * radius);
    }

    static double Wrap(double arc, double total)
    {
        if (total < Tol) return 0;
        var t = arc % total;
        if (t < 0) t += total;
        return t;
    }

    static double Hyp(double x, double y) => Math.Sqrt(x * x + y * y);
}
