namespace CabinetNC.Domain.Geometry;

/// <summary>
/// Collapse real corner/cup fans into OSAI <c>G2/G3 R</c> arcs.
/// Shallow bows (R larger than shop fillets, small sweep) become one <c>G1</c> —
/// they are tessellated straight edges, not part radii.
/// Real lid/opening corners (R≈50, ~90°) stay arcs. Sharp polyline corners stay G1.
/// </summary>
public static class PolylineArcFit
{
    public const double PointTolMm = 0.05;
    public const double MinRadiusMm = 0.5;
    /// <summary>
    /// Shop snap fillets through finger-hole R15. A 20 mm rectangular notch
    /// exit fits G2 R20.8 / R21.96 — that is not a fillet; it must pass the
    /// large-corner gate (≈90°, R22–80) or stay G1.
    /// </summary>
    public const double MaxRadiusMm = 16;
    /// <summary>
    /// Designed outer corners at the tool centre. Lounge lids are R48–R55;
    /// BULVD1-class panels are ~R160 part + Ø10 → ~R165. Leftover long-edge
    /// bows (R70 / 40°) stay G1 via the 70° sweep gate, not this cap.
    /// </summary>
    public const double MaxCornerRadiusMm = 250;
    /// <summary>
    /// Real lid/opening corners are ~90°. Leftover long edges can fit a
    /// 40–50° G2 around R70 — that is a tessellated straight, not a corner.
    /// A designed panel bow (BULVD1 hypot, R~1700 / 40°, sagitta ~100)
    /// still has to become one G2/G3 or the toolpath chords off the CAD.
    /// </summary>
    public const double LargeArcMinSweepDeg = 70;
    public const double LargeArcMinSagittaMm = 4;
    public const double DesignedBowMinSagittaMm = 40;
    public const double DesignedBowMinSweepDeg = 28;
    public const double MinSweepDeg = 8;
    public const double MinSagittaMm = 0.06;

    static readonly double[] SnapRadii =
    [
        3.175, 5, 5.715, 8.255, 14.497, 20.504, 20.584, 21.58,
    ];

    public readonly record struct Seg(bool Arc, bool Cw, double X, double Y, double R);

    public static IReadOnlyList<Seg> Fit(IReadOnlyList<(double X, double Y)> path, bool closed = false)
    {
        var pts = Dedup(path);
        if (closed && pts.Count >= 3)
        {
            var a = pts[0];
            var b = pts[^1];
            if (Math.Abs(a.X - b.X) > 1e-6 || Math.Abs(a.Y - b.Y) > 1e-6)
                pts.Add(a);
        }

        var segs = new List<Seg>();
        if (pts.Count < 2) return segs;
        var i = 0;
        while (i < pts.Count - 1)
        {
            if (TryArc(pts, i, out var end, out var cw, out var r))
            {
                segs.Add(new Seg(true, cw, pts[end].X, pts[end].Y, SnapRadius(r)));
                i = end;
                continue;
            }
            var straight = GrowStraight(pts, i);
            segs.Add(new Seg(false, false, pts[straight].X, pts[straight].Y, 0));
            i = straight;
        }
        return CleanTangents(pts[0], FlattenSpuriousBows(pts[0], MergeSameArcs(pts[0], segs)), closed);
    }

    static List<Seg> MergeSameArcs((double X, double Y) start, IReadOnlyList<Seg> segs)
    {
        var list = new List<Seg>();
        var prev = start;
        var arcStart = start;
        foreach (var s in segs)
        {
            if (s.Arc && list.Count > 0 && list[^1].Arc
                && list[^1].Cw == s.Cw
                && Math.Abs(list[^1].R - s.R) < 0.08
                && TryCenter(arcStart, (s.X, s.Y), s.R, s.Cw, out var cx, out var cy)
                && SweepDeg(arcStart, (s.X, s.Y), cx, cy, s.Cw) <= 180.5)
            {
                list[^1] = s with { R = list[^1].R };
                prev = (s.X, s.Y);
                continue;
            }
            arcStart = prev;
            list.Add(s);
            prev = (s.X, s.Y);
        }
        return list;
    }

    /// <summary>
    /// A 40–60° R10 leftover from a tessellated notch is not a shop fillet.
    /// Real R5 / R3 are 90° or 180° after <see cref="MergeSameArcs"/>.
    /// </summary>
    static List<Seg> FlattenSpuriousBows((double X, double Y) start, IReadOnlyList<Seg> segs)
    {
        var prev = start;
        var list = new List<Seg>(segs.Count);
        foreach (var s in segs)
        {
            if (s.Arc && s.R is > 6 and <= MaxRadiusMm
                && TryCenter(prev, (s.X, s.Y), s.R, s.Cw, out var cx, out var cy))
            {
                var sweep = SweepDeg(prev, (s.X, s.Y), cx, cy, s.Cw);
                if (sweep is > 24 and < 68)
                {
                    list.Add(s with { Arc = false, Cw = false, R = 0 });
                    prev = (s.X, s.Y);
                    continue;
                }
            }
            list.Add(s);
            prev = (s.X, s.Y);
        }
        return list;
    }

    static bool TryCenter(
        (double X, double Y) a, (double X, double Y) b, double r, bool cw,
        out double cx, out double cy)
    {
        cx = cy = 0;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var rr = Math.Abs(r);
        if (d < 1e-9 || d > 2 * rr + 1e-4)
            return false;
        var h = Math.Sqrt(Math.Max(0, rr * rr - (d * 0.5) * (d * 0.5)));
        var mx = (a.X + b.X) * 0.5;
        var my = (a.Y + b.Y) * 0.5;
        var ux = -dy / d;
        var uy = dx / d;
        var c1x = mx + ux * h;
        var c1y = my + uy * h;
        var c2x = mx - ux * h;
        var c2y = my - uy * h;
        var c1cw = (a.X - c1x) * (b.Y - c1y) - (a.Y - c1y) * (b.X - c1x) < 0;
        if (c1cw == cw)
        {
            cx = c1x;
            cy = c1y;
            return true;
        }
        cx = c2x;
        cy = c2y;
        return true;
    }

    /// <summary>
    /// Clipper round-join leaves a 0.1 mm G1 stub before a fitted G2.
    /// Snap line–arc–line fillets back onto the long edges (axis-snap when
    /// the incoming run is within ~2.5° of H/V) so the part corner matches CAD.
    /// </summary>
    public static IReadOnlyList<Seg> CleanTangents(
        (double X, double Y) start,
        IReadOnlyList<Seg> segs,
        bool closed)
    {
        if (segs.Count < 3) return segs;
        var list = segs.ToList();
        var n = list.Count;
        var curStart = start;
        for (var pass = 0; pass < 4; pass++)
        {
            if (closed)
                curStart = (list[^1].X, list[^1].Y);
            var v = Vertices(curStart, list);
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (!list[i].Arc) continue;
                var ip = (i - 1 + n) % n;
                var inx = (i + 1) % n;
                if (!closed && (i == 0 || i == n - 1)) continue;
                if (list[ip].Arc || list[inx].Arc) continue;
                if (v.Count < n + 1) continue;

                var a0 = v[ip];
                var a1 = v[i];
                var b0 = v[i + 1];
                var b1 = v[inx + 1];
                var ipLong = ip;
                // Clipper leaves 0.2–2 mm G1 stubs before the R5 (B3 T-join).
                // Snap from the long inbound edge, then collapse the stubs.
                if (!list[ip].Arc && Dist(a0, a1) < 2.6)
                {
                    var back = (ip - 1 + n) % n;
                    if ((closed || ip > 0) && !list[back].Arc && Dist(v[back], v[ip]) >= 8)
                    {
                        a0 = v[back];
                        ipLong = back;
                    }
                }
                var large = list[i].R > MaxRadiusMm + 0.05;
                if (!TryFillet(a0, a1, b0, b1, list[i].R, list[i].Cw, large, out var t0, out var t1))
                    continue;
                var d0 = Dist(t0, a1);
                var d1 = Dist(t1, b0);
                if (d0 < 2e-4 && d1 < 2e-4)
                    continue;
                var snap = large ? Math.Max(2.6, 0.12 * list[i].R) : 2.6;
                if (d0 > snap || d1 > snap)
                    continue;

                list[ip] = list[ip] with { X = t0.X, Y = t0.Y };
                if (ipLong != ip)
                    list[ipLong] = list[ipLong] with { X = t0.X, Y = t0.Y };
                list[i] = list[i] with { X = t1.X, Y = t1.Y };
                changed = true;
            }
            if (!changed) break;
        }
        return list;
    }

    static List<(double X, double Y)> Vertices((double X, double Y) start, IReadOnlyList<Seg> segs)
    {
        var v = new List<(double X, double Y)>(segs.Count + 1) { start };
        foreach (var s in segs)
            v.Add((s.X, s.Y));
        return v;
    }

    static bool TryFillet(
        (double X, double Y) a0,
        (double X, double Y) a1,
        (double X, double Y) b0,
        (double X, double Y) b1,
        double r,
        bool cw,
        bool allowSkew,
        out (double X, double Y) t0,
        out (double X, double Y) t1)
    {
        t0 = a1;
        t1 = b0;
        if (r < MinRadiusMm) return false;
        var d1 = SnapDir(a1.X - a0.X, a1.Y - a0.Y);
        var d2 = SnapDir(b1.X - b0.X, b1.Y - b0.Y);
        if (d1.X * d1.X + d1.Y * d1.Y < 0.5) return false;
        if (d2.X * d2.X + d2.Y * d2.Y < 0.5) return false;

        var n1 = cw ? (d1.Y, -d1.X) : (-d1.Y, d1.X);
        var n2 = cw ? (d2.Y, -d2.X) : (-d2.Y, d2.X);
        // Incoming is dirty at the corner (a1); keep the far start a0.
        // Outgoing is dirty at the far next-corner (b1); keep the arc end b0.
        var p1 = (a0.X + r * n1.Item1, a0.Y + r * n1.Item2);
        var p2 = (b0.X + r * n2.Item1, b0.Y + r * n2.Item2);
        if (!Intersect(p1, d1, p2, d2, out var c))
            return false;

        t0 = (c.X - r * n1.Item1, c.Y - r * n1.Item2);
        t1 = (c.X - r * n2.Item1, c.Y - r * n2.Item2);
        if (Math.Abs(Dist(t0, c) - r) > 0.05 || Math.Abs(Dist(t1, c) - r) > 0.05)
            return false;

        var along1 = (t0.X - a0.X) * d1.X + (t0.Y - a0.Y) * d1.Y;
        var along2 = (b1.X - t1.X) * d2.X + (b1.Y - t1.Y) * d2.Y;
        if (along1 < 1 || along2 < 1)
            return false;

        var sweep = SweepDeg(t0, t1, c.X, c.Y, cw);
        if (sweep is < 70 or > 110)
            return false;
        var axis = Math.Abs(d1.X) > 0.999 && Math.Abs(d2.Y) > 0.999
            || Math.Abs(d1.Y) > 0.999 && Math.Abs(d2.X) > 0.999;
        return axis || allowSkew;
    }

    static (double X, double Y) SnapDir(double dx, double dy)
    {
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return (0, 0);
        var ux = dx / len;
        var uy = dy / len;
        if (Math.Abs(ux) > 0.999) return (Math.Sign(dx), 0);
        if (Math.Abs(uy) > 0.999) return (0, Math.Sign(dy));
        return (ux, uy);
    }

    static bool Intersect(
        (double X, double Y) p1, (double X, double Y) d1,
        (double X, double Y) p2, (double X, double Y) d2,
        out (double X, double Y) hit)
    {
        hit = default;
        var cross = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(cross) < 1e-12) return false;
        var t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / cross;
        hit = (p1.X + t * d1.X, p1.Y + t * d1.Y);
        return true;
    }

    static bool TryArc(
        IReadOnlyList<(double X, double Y)> pts,
        int i,
        out int end,
        out bool cw,
        out double r)
    {
        end = i;
        cw = false;
        r = 0;
        if (i + 2 >= pts.Count) return false;

        var bestEnd = -1;
        double bestR = 0, bestCx = 0, bestCy = 0;
        var bestCw = false;

        for (var j = i + 2; j < pts.Count; j++)
        {
            var mid = (i + j) / 2;
            if (mid <= i) mid = i + 1;
            if (mid >= j) mid = j - 1;
            if (!CircleThrough(pts[i], pts[mid], pts[j], out var cx, out var cy, out var rr, out var ccw)
                && !CircleThrough(pts[i], pts[i + 1], pts[j], out cx, out cy, out rr, out ccw))
            {
                if (bestEnd >= 0) break;
                continue;
            }
            if (!AcceptArc(pts, i, j, cx, cy, rr, ccw))
            {
                if (bestEnd >= 0) break;
                continue;
            }
            bestEnd = j;
            bestR = rr;
            bestCw = ccw;
            bestCx = cx;
            bestCy = cy;
        }

        if (bestEnd < i + 2) return false;
        end = bestEnd;
        r = bestR;
        cw = bestCw;

        var sweepDeg = SweepDeg(pts[i], pts[end], bestCx, bestCy, cw);
        if (end == i + 2)
        {
            var l0 = Dist(pts[i], pts[i + 1]);
            var l1 = Dist(pts[i + 1], pts[i + 2]);
            var lo = Math.Min(l0, l1);
            if (lo < 1e-9) return false;
            // Uneven two-chord is usually a line + stub. A designed panel
            // bow (BULVD1 855+337 mm on R1714) is allowed; a 800 mm edge
            // plus the first 0.6 mm of an R5 fan is not.
            if (Math.Max(l0, l1) / lo > 1.8
                && (!IsRealLargeCorner(r, sweepDeg) || lo < 40))
                return false;
        }

        var sagitta = r * (1 - Math.Cos(sweepDeg * Math.PI / 360));
        if (sweepDeg < MinSweepDeg && sagitta < MinSagittaMm) return false;
        if (sagitta < MinSagittaMm && end < i + 3) return false;
        return true;
    }

    /// <summary>
    /// Collapse a tessellated shallow bow (R &gt; <see cref="MaxRadiusMm"/>) or
    /// a colinear run into one chord. Stops before a real corner fan.
    /// </summary>
    static int GrowStraight(IReadOnlyList<(double X, double Y)> pts, int i)
    {
        var end = i + 1;
        for (var j = i + 2; j < pts.Count; j++)
        {
            if (!IsStraightish(pts, i, j))
                break;
            end = j;
        }
        return end;
    }

    static bool IsStraightish(IReadOnlyList<(double X, double Y)> pts, int i, int j)
    {
        if (j <= i + 1) return true;
        if (HasSharpCorner(pts, i, j))
            return false;
        if (AllOnChord(pts, i, j))
            return true;
        var mid = (i + j) / 2;
        if (mid <= i) mid = i + 1;
        if (mid >= j) mid = j - 1;
        if (!CircleThrough(pts[i], pts[mid], pts[j], out var cx, out var cy, out var r, out var cw)
            && !CircleThrough(pts[i], pts[i + 1], pts[j], out cx, out cy, out r, out cw))
            return false;
        if (r <= MaxRadiusMm)
            return false;
        var sweep = SweepDeg(pts[i], pts[j], cx, cy, cw);
        if (IsRealLargeCorner(r, sweep))
            return false;
        // First few chords of an R40–80 lid/rebate corner look like a shallow
        // bow (sweep ≪ 40°). Do not swallow that lead-in as G1.
        if (r <= MaxCornerRadiusMm && Dist(pts[i], pts[j]) < 1.6 * r)
            return false;
        return AllOnCircle(pts, i, j, cx, cy, r) && SameTurn(pts, i, j, cw);
    }

    static bool IsRealLargeCorner(double r, double sweepDeg)
    {
        if (r <= MaxRadiusMm)
            return false;
        var sagitta = r * (1 - Math.Cos(sweepDeg * Math.PI / 360));
        if (sagitta < LargeArcMinSagittaMm)
            return false;
        if (r <= MaxCornerRadiusMm && sweepDeg >= LargeArcMinSweepDeg)
            return true;
        return sagitta >= DesignedBowMinSagittaMm && sweepDeg >= DesignedBowMinSweepDeg;
    }

    static bool HasSharpCorner(IReadOnlyList<(double X, double Y)> pts, int i, int j)
    {
        const double maxTurnDeg = 18;
        for (var k = i + 1; k < j; k++)
        {
            var ax = pts[k].X - pts[k - 1].X;
            var ay = pts[k].Y - pts[k - 1].Y;
            var bx = pts[k + 1].X - pts[k].X;
            var by = pts[k + 1].Y - pts[k].Y;
            var d0 = Math.Sqrt(ax * ax + ay * ay);
            var d1 = Math.Sqrt(bx * bx + by * by);
            if (d0 < 1e-9 || d1 < 1e-9) continue;
            var cross = ax * by - ay * bx;
            var dot = ax * bx + ay * by;
            var deg = Math.Abs(Math.Atan2(cross, dot)) * 180 / Math.PI;
            if (deg > maxTurnDeg) return true;
        }
        return false;
    }

    static bool AllOnChord(IReadOnlyList<(double X, double Y)> pts, int i, int j)
    {
        var a = pts[i];
        var b = pts[j];
        var chord = Dist(a, b);
        if (chord < 1e-9) return false;
        for (var k = i + 1; k < j; k++)
        {
            if (DistToSegment(pts[k], a, b) > PointTolMm)
                return false;
        }
        return true;
    }

    static double DistToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return Dist(p, a);
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        return Dist(p, (a.X + t * dx, a.Y + t * dy));
    }

    static bool AcceptArc(
        IReadOnlyList<(double X, double Y)> pts,
        int i,
        int j,
        double cx, double cy, double r, bool cw)
    {
        if (r < MinRadiusMm) return false;
        var sweep = SweepDeg(pts[i], pts[j], cx, cy, cw);
        if (sweep > 180.5) return false;
        if (r > MaxRadiusMm && !IsRealLargeCorner(r, sweep)) return false;
        // Designed hypot bows are a few long G1s. A long edge plus the
        // first ticks of an R5 fan can fit R>1000 with a 40 mm sagitta.
        if (r > MaxCornerRadiusMm && !AllLong(pts, i, j, 40)) return false;
        if (!AllOnCircle(pts, i, j, cx, cy, r, FitTolMm(r))) return false;
        if (!SameTurn(pts, i, j, cw)) return false;
        if (!AllShort(pts, i, j, r)) return false;
        return true;
    }

    /// <summary>
    /// Clipper round-join on a tessellated opening is not a perfect circle.
    /// Tight 0.05 mm rejects the inner rebate wall and leaves a G1 stair.
    /// </summary>
    static double FitTolMm(double r) =>
        Math.Max(PointTolMm, Math.Min(1.25, 0.028 * Math.Max(r, 1)));

    static bool AllOnCircle(
        IReadOnlyList<(double X, double Y)> pts, int i, int j, double cx, double cy, double r,
        double? tolMm = null)
    {
        var tol = tolMm ?? PointTolMm;
        for (var k = i; k <= j; k++)
            if (!OnCircle(pts[k], cx, cy, r, tol)) return false;
        return true;
    }

    static bool SameTurn(IReadOnlyList<(double X, double Y)> pts, int i, int j, bool cw)
    {
        for (var k = i + 1; k < j; k++)
        {
            var ax = pts[k].X - pts[k - 1].X;
            var ay = pts[k].Y - pts[k - 1].Y;
            var bx = pts[k + 1].X - pts[k].X;
            var by = pts[k + 1].Y - pts[k].Y;
            var d0 = Math.Sqrt(ax * ax + ay * ay);
            var d1 = Math.Sqrt(bx * bx + by * by);
            if (d0 < 1e-9 || d1 < 1e-9) continue;
            var turn = ax * by - ay * bx;
            // Clipper round-join can tick <0.3° the wrong way on a 5 mm chord.
            if (Math.Abs(turn) < 0.08) continue;
            if (cw ? turn > 0 : turn < 0) return false;
        }
        return true;
    }

    static bool CircleThrough(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c,
        out double cx, out double cy, out double r, out bool cw)
    {
        cx = cy = r = 0;
        cw = false;
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return false;
        var a2 = a.X * a.X + a.Y * a.Y;
        var b2 = b.X * b.X + b.Y * b.Y;
        var c2 = c.X * c.X + c.Y * c.Y;
        cx = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
        cy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
        r = Dist(a, (cx, cy));
        if (r < 1e-9) return false;
        cw = Cross(a, b, c) < 0;
        return true;
    }

    static bool OnCircle((double X, double Y) p, double cx, double cy, double r, double? tolMm = null) =>
        Math.Abs(Dist(p, (cx, cy)) - r) <= (tolMm ?? PointTolMm);

    static bool AllShort(IReadOnlyList<(double X, double Y)> pts, int i, int end, double r)
    {
        for (var k = i; k < end; k++)
            if (!ShortChord(pts[k], pts[k + 1], r)) return false;
        return true;
    }

    static bool AllLong(IReadOnlyList<(double X, double Y)> pts, int i, int end, double minMm)
    {
        for (var k = i; k < end; k++)
            if (Dist(pts[k], pts[k + 1]) < minMm) return false;
        return true;
    }

    static bool ShortChord((double X, double Y) a, (double X, double Y) b, double r)
    {
        // Shop R5 fans are dense. A 90° designed corner tessellated as two
        // chords is 2 r sin(22.5°) ≈ 0.765 r — 0.72 rejected BULVD1 R165.
        var span = r <= MaxRadiusMm ? 0.72 * r : 0.85 * r;
        return Dist(a, b) <= Math.Max(2.5, span);
    }

    static double SweepDeg(
        (double X, double Y) start,
        (double X, double Y) end,
        double cx, double cy, bool cw)
    {
        var a0 = Math.Atan2(start.Y - cy, start.X - cx);
        var a1 = Math.Atan2(end.Y - cy, end.X - cx);
        var d = a1 - a0;
        if (cw)
        {
            if (d > 0) d -= 2 * Math.PI;
            return -d * 180 / Math.PI;
        }
        if (d < 0) d += 2 * Math.PI;
        return d * 180 / Math.PI;
    }

    static double SnapRadius(double r)
    {
        foreach (var s in SnapRadii)
            if (Math.Abs(r - s) <= 0.05) return s;
        return Math.Round(r, 4, MidpointRounding.AwayFromZero);
    }

    static List<(double X, double Y)> Dedup(IReadOnlyList<(double X, double Y)> path)
    {
        var pts = new List<(double X, double Y)>(path.Count);
        foreach (var p in path)
        {
            if (pts.Count > 0 && Dist(pts[^1], p) < 1e-4) continue;
            pts.Add(p);
        }
        return pts;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
}
