namespace CabinetNC.Domain.Geometry;

/// <summary>How a new centreline relates to an existing one.</summary>
public enum CenterlineOverlap
{
    None,
    /// <summary>Proper crossing / T-junction — both stay, CAM cuts both.</summary>
    Crossing,
    /// <summary>Collinear with &gt; 1mm shared length — kept, but the user is told.</summary>
    Collinear,
    /// <summary>Same polyline within 0.5mm — refuse, it would be an invisible double.</summary>
    Duplicate,
}

/// <summary>
/// Pure polyline maths for TRIM / EXTEND / LINE on groove centrelines. Parameters along a
/// polyline are <c>segmentIndex + u</c>, so a 3-point path spans t ∈ [0, 2].
/// </summary>
public static class CenterlineOps
{
    const double Eps = 1e-6;
    /// <summary>Pieces shorter than this after a cut are dropped as slivers.</summary>
    public const double MinPieceMm = 1.0;
    /// <summary>Collinear sharing below this is treated as touching, not overlapping.</summary>
    public const double CollinearMinMm = 1.0;
    public const double DuplicateTolMm = 0.5;

    public static double Length(IReadOnlyList<Point2> path)
    {
        var len = 0.0;
        for (var i = 1; i < path.Count; i++) len += Dist(path[i - 1], path[i]);
        return len;
    }

    public static Point2 At(IReadOnlyList<Point2> path, double t)
    {
        if (path.Count == 0) return default;
        if (path.Count == 1 || t <= 0) return path[0];
        var n = path.Count - 1;
        if (t >= n) return path[^1];
        var i = (int)Math.Floor(t);
        var u = t - i;
        var a = path[i];
        var b = path[i + 1];
        return new Point2(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
    }

    /// <summary>Parameter of the point on <paramref name="path"/> nearest to <paramref name="p"/>.</summary>
    public static double NearestParam(IReadOnlyList<Point2> path, Point2 p)
    {
        if (path.Count < 2) return 0;
        var bestT = 0.0;
        var bestD = double.MaxValue;
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var len2 = vx * vx + vy * vy;
            var u = len2 < Eps ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
            var qx = a.X + vx * u - p.X;
            var qy = a.Y + vy * u - p.Y;
            var d = qx * qx + qy * qy;
            if (d < bestD)
            {
                bestD = d;
                bestT = i - 1 + u;
            }
        }
        return bestT;
    }

    public static double DistanceTo(IReadOnlyList<Point2> path, Point2 p) =>
        Dist(At(path, NearestParam(path, p)), p);

    /// <summary>Drop a repeated closing vertex so the ring has one vertex per corner.</summary>
    public static List<Point2> OpenRing(IReadOnlyList<Point2> pts)
    {
        var ring = pts.Where((_, i) => i == 0 || Dist(pts[i], pts[i - 1]) > 1e-6).ToList();
        if (ring.Count >= 2 && Dist(ring[0], ring[^1]) < 1e-6) ring.RemoveAt(ring.Count - 1);
        return ring;
    }

    public static List<Point2> CloseRing(IReadOnlyList<Point2> verts)
    {
        var ring = OpenRing(verts);
        if (ring.Count >= 1) ring.Add(ring[0]);
        return ring;
    }

    /// <summary>Signed area (CCW positive). Closing vertex optional.</summary>
    public static double SignedArea(IReadOnlyList<Point2> ring)
    {
        var pts = OpenRing(ring);
        if (pts.Count < 3) return 0;
        var a = 0.0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            a += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
        return a * 0.5;
    }

    public readonly record struct RingHit(double RingT, double CutterT, Point2 At);

    /// <summary>
    /// Intersections of an open cutter with a closed ring, including vertices. Ring parameter
    /// is <c>[0, n)</c> on the n-edge loop.
    /// </summary>
    public static List<RingHit> RingHits(IReadOnlyList<Point2> ring, IReadOnlyList<Point2> cutter)
    {
        var verts = OpenRing(ring);
        var hits = new List<RingHit>();
        if (verts.Count < 3 || cutter.Count < 2) return hits;
        var n = verts.Count;
        for (var i = 0; i < n; i++)
        {
            var a0 = verts[i];
            var a1 = verts[(i + 1) % n];
            for (var j = 1; j < cutter.Count; j++)
            {
                foreach (var hit in SegmentHits(a0, a1, cutter[j - 1], cutter[j]))
                {
                    var t = i + hit.U;
                    if (t >= n - 1e-9) t = 0;
                    hits.Add(new RingHit(t, j - 1 + hit.V, Lerp(a0, a1, hit.U)));
                }
            }
        }
        hits.Sort((a, b) => a.RingT.CompareTo(b.RingT));
        var result = new List<RingHit>();
        foreach (var h in hits)
        {
            if (result.Count > 0 && Dist(result[^1].At, h.At) < 1e-3) continue;
            result.Add(h);
        }
        if (result.Count >= 2 && Dist(result[0].At, result[^1].At) < 1e-3)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>
    /// Cut a closed ring with an open polyline that crosses it twice. Keeps the larger loop
    /// (the remaining board after a corner cut or a through-cut).
    /// </summary>
    public static List<Point2>? NotchRing(IReadOnlyList<Point2> ring, IReadOnlyList<Point2> cutter)
    {
        var hits = RingHits(ring, cutter);
        if (hits.Count != 2) return null;
        var a = hits[0];
        var b = hits[1];
        var loop1 = Join(RingArc(ring, a.RingT, b.RingT), PathArc(cutter, b.CutterT, a.CutterT));
        var loop2 = Join(RingArc(ring, b.RingT, a.RingT), PathArc(cutter, a.CutterT, b.CutterT));
        var area1 = Math.Abs(SignedArea(loop1));
        var area2 = Math.Abs(SignedArea(loop2));
        if (area1 < 1 && area2 < 1) return null;
        var keep = area1 >= area2 ? loop1 : loop2;
        return OpenRing(keep);
    }

    /// <summary>
    /// TRIM a closed ring: drop the piece under <paramref name="pick"/> between consecutive
    /// cutter hits and bridge the gap (along the cutter when both hits share one).
    /// </summary>
    public static List<Point2>? TrimClosedRing(
        IReadOnlyList<Point2> ring,
        IEnumerable<IReadOnlyList<Point2>> cutters,
        Point2 pick)
    {
        var tagged = new List<(RingHit Hit, IReadOnlyList<Point2> Cutter)>();
        foreach (var c in cutters)
        {
            if (c is null || c.Count < 2) continue;
            foreach (var h in RingHits(ring, c))
                tagged.Add((h, c));
        }
        if (tagged.Count < 2) return null;
        tagged.Sort((a, b) => a.Hit.RingT.CompareTo(b.Hit.RingT));
        var uniq = new List<(RingHit Hit, IReadOnlyList<Point2> Cutter)>();
        foreach (var t in tagged)
        {
            if (uniq.Count > 0 && Dist(uniq[^1].Hit.At, t.Hit.At) < 1e-3) continue;
            uniq.Add(t);
        }
        if (uniq.Count >= 2 && Dist(uniq[0].Hit.At, uniq[^1].Hit.At) < 1e-3)
            uniq.RemoveAt(uniq.Count - 1);
        if (uniq.Count < 2) return null;

        var closed = CloseRing(ring);
        var n = OpenRing(ring).Count;
        var tPick = NearestParam(closed, pick);
        if (tPick >= n - 1e-9) tPick = 0;

        var drop = 0;
        for (var i = 0; i < uniq.Count; i++)
        {
            var t0 = uniq[i].Hit.RingT;
            var t1 = uniq[(i + 1) % uniq.Count].Hit.RingT;
            var inside = t1 > t0 + 1e-9
                ? tPick + 1e-9 >= t0 && tPick < t1 - 1e-9
                : tPick + 1e-9 >= t0 || tPick < t1 - 1e-9;
            if (inside) { drop = i; break; }
        }
        var start = uniq[drop];
        var end = uniq[(drop + 1) % uniq.Count];
        var keptArc = RingArc(ring, end.Hit.RingT, start.Hit.RingT);
        IReadOnlyList<Point2> bridge;
        if (ReferenceEquals(start.Cutter, end.Cutter))
            bridge = PathArc(start.Cutter, start.Hit.CutterT, end.Hit.CutterT);
        else
            bridge = [start.Hit.At, end.Hit.At];
        var loop = OpenRing(Join(keptArc, bridge));
        return Math.Abs(SignedArea(loop)) < 1 ? null : loop;
    }

    static List<Point2> RingArc(IReadOnlyList<Point2> ring, double t0, double t1)
    {
        var closed = CloseRing(ring);
        var n = closed.Count - 1;
        if (n < 1) return [];
        t0 = NormalizeT(t0, n);
        t1 = NormalizeT(t1, n);
        if (t1 + 1e-9 >= t0 && Math.Abs(t1 - t0) > 1e-9)
            return SubPath(closed, t0, t1);
        // Wrap: t0 → n, then 0 → t1.
        var a = SubPath(closed, t0, n);
        var b = t1 < 1e-9 ? [] : SubPath(closed, 0, t1);
        return Join(a, b);
    }

    static List<Point2> PathArc(IReadOnlyList<Point2> path, double t0, double t1)
    {
        if (path.Count < 2) return path.ToList();
        if (t1 + 1e-9 >= t0)
            return SubPath(path, t0, t1);
        var fwd = SubPath(path, t1, t0);
        fwd.Reverse();
        return fwd;
    }

    static List<Point2> Join(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        var pts = a.ToList();
        foreach (var p in b)
        {
            if (pts.Count > 0 && Dist(pts[^1], p) < 1e-6) continue;
            pts.Add(p);
        }
        return pts;
    }

    static double NormalizeT(double t, int n)
    {
        if (n <= 0) return 0;
        t %= n;
        if (t < 0) t += n;
        if (t >= n - 1e-9) t = 0;
        return t;
    }

    static Point2 Lerp(Point2 a, Point2 b, double u) =>
        new(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);

    /// <summary>
    /// Sorted, de-duplicated cut parameters on <paramref name="target"/> where any cutter
    /// polyline touches it. Collinear overlaps contribute both interval ends. Hits at the very
    /// ends of the target are dropped — cutting there changes nothing.
    /// </summary>
    public static List<double> CutParams(IReadOnlyList<Point2> target, IEnumerable<IReadOnlyList<Point2>> cutters)
    {
        var hits = new List<double>();
        if (target.Count < 2) return hits;
        foreach (var cutter in cutters)
        {
            if (cutter is null || cutter.Count < 2) continue;
            for (var i = 1; i < target.Count; i++)
            {
                for (var j = 1; j < cutter.Count; j++)
                {
                    foreach (var hit in SegmentHits(target[i - 1], target[i], cutter[j - 1], cutter[j]))
                        hits.Add(i - 1 + hit.U);
                }
            }
        }
        var n = target.Count - 1;
        hits.Sort();
        var result = new List<double>();
        foreach (var t in hits)
        {
            if (t < 1e-4 || t > n - 1e-4) continue;
            if (result.Count > 0 && Math.Abs(result[^1] - t) < 1e-4) continue;
            // Same physical point may appear as (i, 1.0) and (i+1, 0.0).
            if (result.Count > 0 && Dist(At(target, result[^1]), At(target, t)) < 1e-3) continue;
            result.Add(t);
        }
        return result;
    }

    /// <summary>Split at the given (sorted) params; each piece keeps its own vertex list.</summary>
    public static List<List<Point2>> Split(IReadOnlyList<Point2> path, IReadOnlyList<double> cuts)
    {
        var pieces = new List<List<Point2>>();
        if (path.Count < 2) return pieces;
        var n = path.Count - 1;
        var bounds = new List<double> { 0 };
        bounds.AddRange(cuts.Where(t => t > 1e-4 && t < n - 1e-4));
        bounds.Add(n);
        for (var k = 1; k < bounds.Count; k++)
            pieces.Add(SubPath(path, bounds[k - 1], bounds[k]));
        return pieces;
    }

    static List<Point2> SubPath(IReadOnlyList<Point2> path, double t0, double t1)
    {
        var pts = new List<Point2> { At(path, t0) };
        var firstVertex = (int)Math.Floor(t0) + 1;
        var lastVertex = (int)Math.Ceiling(t1) - 1;
        for (var i = firstVertex; i <= lastVertex && i < path.Count; i++)
        {
            if (Dist(pts[^1], path[i]) > 1e-6) pts.Add(path[i]);
        }
        var end = At(path, t1);
        if (Dist(pts[^1], end) > 1e-6) pts.Add(end);
        return pts;
    }

    /// <summary>
    /// TRIM: remove the piece of <paramref name="path"/> under <paramref name="pick"/>, cutting at
    /// every cutter intersection. Returns the surviving pieces (slivers dropped) or null when no
    /// cutter touches the path.
    /// </summary>
    public static List<List<Point2>>? Trim(IReadOnlyList<Point2> path, IEnumerable<IReadOnlyList<Point2>> cutters, Point2 pick)
    {
        var cuts = CutParams(path, cutters);
        if (cuts.Count == 0) return null;
        var pieces = Split(path, cuts);
        var tPick = NearestParam(path, pick);
        var bounds = new List<double> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(path.Count - 1);
        var drop = 0;
        for (var k = 1; k < bounds.Count; k++)
        {
            if (tPick <= bounds[k] + 1e-9) { drop = k - 1; break; }
            drop = k - 1;
        }
        var kept = new List<List<Point2>>();
        for (var k = 0; k < pieces.Count; k++)
        {
            if (k == drop) continue;
            if (Length(pieces[k]) < MinPieceMm) continue;
            kept.Add(pieces[k]);
        }
        return kept;
    }

    /// <summary>
    /// EXTEND: push the end of <paramref name="path"/> nearest to <paramref name="pick"/> along its
    /// last segment until it meets a boundary. Returns null when nothing lies ahead.
    /// </summary>
    public static List<Point2>? Extend(IReadOnlyList<Point2> path, IEnumerable<IReadOnlyList<Point2>> boundaries, Point2 pick)
    {
        if (path.Count < 2) return null;
        var atStart = Dist(path[0], pick) < Dist(path[^1], pick);
        var tip = atStart ? path[0] : path[^1];
        var prev = atStart ? path[1] : path[^2];
        var dx = tip.X - prev.X;
        var dy = tip.Y - prev.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < Eps) return null;
        dx /= len;
        dy /= len;

        var best = double.MaxValue;
        foreach (var b in boundaries)
        {
            if (b is null || b.Count < 2) continue;
            for (var j = 1; j < b.Count; j++)
            {
                var s = RayHit(tip, dx, dy, b[j - 1], b[j]);
                if (s is { } d && d > 1e-3 && d < best) best = d;
            }
        }
        if (best == double.MaxValue) return null;
        var newTip = new Point2(tip.X + dx * best, tip.Y + dy * best);
        var pts = path.ToList();
        if (atStart) pts[0] = newTip;
        else pts[^1] = newTip;
        return pts;
    }

    /// <summary>Relationship between a new centreline and an existing one.</summary>
    public static CenterlineOverlap Classify(IReadOnlyList<Point2> fresh, IReadOnlyList<Point2> existing)
    {
        if (fresh.Count < 2 || existing.Count < 2) return CenterlineOverlap.None;
        if (IsDuplicate(fresh, existing)) return CenterlineOverlap.Duplicate;

        var collinear = 0.0;
        var crossing = false;
        for (var i = 1; i < fresh.Count; i++)
        {
            for (var j = 1; j < existing.Count; j++)
            {
                var shared = CollinearSharedLength(fresh[i - 1], fresh[i], existing[j - 1], existing[j]);
                if (shared > 0)
                {
                    collinear += shared;
                    continue;
                }
                if (SegmentHits(fresh[i - 1], fresh[i], existing[j - 1], existing[j]).Count > 0)
                    crossing = true;
            }
        }
        if (collinear > CollinearMinMm) return CenterlineOverlap.Collinear;
        return crossing ? CenterlineOverlap.Crossing : CenterlineOverlap.None;
    }

    static bool IsDuplicate(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        // Every vertex of each lies on the other and the ends match (either direction).
        bool EndsMatch(IReadOnlyList<Point2> p, IReadOnlyList<Point2> q) =>
            (Dist(p[0], q[0]) < DuplicateTolMm && Dist(p[^1], q[^1]) < DuplicateTolMm)
            || (Dist(p[0], q[^1]) < DuplicateTolMm && Dist(p[^1], q[0]) < DuplicateTolMm);
        if (!EndsMatch(a, b)) return false;
        foreach (var p in a) if (DistanceTo(b, p) > DuplicateTolMm) return false;
        foreach (var p in b) if (DistanceTo(a, p) > DuplicateTolMm) return false;
        return true;
    }

    /// <summary>Length of the overlap when two segments are collinear, else 0.</summary>
    static double CollinearSharedLength(Point2 a0, Point2 a1, Point2 b0, Point2 b1)
    {
        var rx = a1.X - a0.X;
        var ry = a1.Y - a0.Y;
        var rr = rx * rx + ry * ry;
        if (rr < Eps) return 0;
        var sx = b1.X - b0.X;
        var sy = b1.Y - b0.Y;
        var rlen = Math.Sqrt(rr);
        if (Math.Abs(rx * sy - ry * sx) > 1e-6 * rlen * Math.Sqrt(sx * sx + sy * sy)) return 0;
        // Perpendicular distance of b0 from line a must be ~0.
        if (Math.Abs(rx * (b0.Y - a0.Y) - ry * (b0.X - a0.X)) / rlen > 0.05) return 0;
        var t0 = ((b0.X - a0.X) * rx + (b0.Y - a0.Y) * ry) / rr;
        var t1 = ((b1.X - a0.X) * rx + (b1.Y - a0.Y) * ry) / rr;
        var lo = Math.Max(0, Math.Min(t0, t1));
        var hi = Math.Min(1, Math.Max(t0, t1));
        return hi > lo ? (hi - lo) * rlen : 0;
    }

    /// <summary>Params (u on a, v on b) where the segments touch. Collinear overlap → interval ends.</summary>
    static List<(double U, double V)> SegmentHits(Point2 a0, Point2 a1, Point2 b0, Point2 b1)
    {
        var res = new List<(double U, double V)>(2);
        var rx = a1.X - a0.X;
        var ry = a1.Y - a0.Y;
        var sx = b1.X - b0.X;
        var sy = b1.Y - b0.Y;
        var denom = rx * sy - ry * sx;
        var qx = b0.X - a0.X;
        var qy = b0.Y - a0.Y;
        var rr = rx * rx + ry * ry;
        var ss = sx * sx + sy * sy;
        if (rr < Eps) return res;

        if (Math.Abs(denom) < 1e-9 * Math.Sqrt(rr) * Math.Sqrt(ss + Eps))
        {
            // Parallel. Collinear only when b0 sits on line a.
            if (Math.Abs(qx * ry - qy * rx) / Math.Sqrt(rr) > 0.05) return res;
            var t0 = (qx * rx + qy * ry) / rr;
            var t1 = ((b1.X - a0.X) * rx + (b1.Y - a0.Y) * ry) / rr;
            var lo = Math.Min(t0, t1);
            var hi = Math.Max(t0, t1);
            if (hi < -Eps || lo > 1 + Eps) return res;
            lo = Math.Clamp(lo, 0, 1);
            hi = Math.Clamp(hi, 0, 1);
            double VAt(double u)
            {
                if (ss < Eps) return 0;
                var p = Lerp(a0, a1, u);
                return Math.Clamp(((p.X - b0.X) * sx + (p.Y - b0.Y) * sy) / ss, 0, 1);
            }
            res.Add((lo, VAt(lo)));
            if (hi - lo > Eps) res.Add((hi, VAt(hi)));
            return res;
        }

        var u = (qx * sy - qy * sx) / denom;
        var v = (qx * ry - qy * rx) / denom;
        if (u < -1e-9 || u > 1 + 1e-9 || v < -1e-9 || v > 1 + 1e-9) return res;
        res.Add((Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1)));
        return res;
    }

    /// <summary>Distance along the ray (origin, dir) to segment b, or null.</summary>
    static double? RayHit(Point2 o, double dx, double dy, Point2 b0, Point2 b1)
    {
        var sx = b1.X - b0.X;
        var sy = b1.Y - b0.Y;
        var denom = dx * sy - dy * sx;
        var qx = b0.X - o.X;
        var qy = b0.Y - o.Y;
        if (Math.Abs(denom) < 1e-12)
        {
            // Parallel: collinear segment ahead → nearest endpoint ahead.
            if (Math.Abs(qx * dy - qy * dx) > 0.05) return null;
            var d0 = qx * dx + qy * dy;
            var d1 = (b1.X - o.X) * dx + (b1.Y - o.Y) * dy;
            var ahead = new[] { d0, d1 }.Where(d => d > 1e-3).DefaultIfEmpty(double.NaN).Min();
            return double.IsNaN(ahead) ? null : ahead;
        }
        var s = (qx * sy - qy * sx) / denom; // along ray
        var v = (qx * dy - qy * dx) / denom; // along segment
        if (v < -1e-9 || v > 1 + 1e-9 || s < 0) return null;
        return s;
    }

    static double Dist(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
