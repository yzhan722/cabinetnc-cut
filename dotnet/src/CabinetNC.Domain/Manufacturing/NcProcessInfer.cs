namespace CabinetNC.Domain.Manufacturing;

/// <summary>L2: group L1 strokes into CutOps by tool, Z, and open/closed path.</summary>
public static class NcProcessInfer
{
    const double ClosedTolMm = 0.6;
    const double PointTolMm = 0.35;
    const double FirstLeaveLo = 0.25;
    const double FirstLeaveHi = 0.85;
    const double ThroughHi = 0.12;
    const double BlindLo = 0.85;

    public static IReadOnlyList<CutOp> Infer(OsaiReplay replay, double thicknessMm = 18)
    {
        var th = thicknessMm > 0 ? thicknessMm : 18;
        var safeZ = replay.SafeZMm > 0 ? replay.SafeZMm : TroyRecipe.SafeZMm;
        var runs = SplitRuns(replay.Strokes, safeZ);
        var ops = new List<CutOp>();
        var n = 0;
        foreach (var run in runs)
        {
            n++;
            var op = Classify(run, n, th, safeZ);
            if (op is not null)
                ops.Add(op);
        }
        return MergeTwoPassContours(ops);
    }

    sealed class CutRun
    {
        public int ToolNum;
        public double WorkZ;
        public List<(double X, double Y)> Path = [];
        public bool HasXy;
    }

    static List<CutRun> SplitRuns(IReadOnlyList<ToolStroke> strokes, double safeZ)
    {
        var runs = new List<CutRun>();
        CutRun? cur = null;
        foreach (var s in strokes)
        {
            var cutZ = Math.Min(s.Z0, s.Z1);
            var isCut = !s.Rapid && cutZ < safeZ - 8;
            if (!isCut)
            {
                cur = null;
                continue;
            }

            if (cur is null || cur.ToolNum != s.ToolNum || Math.Abs(cur.WorkZ - s.Z1) > 0.15)
            {
                cur = new CutRun { ToolNum = s.ToolNum, WorkZ = s.Z1 };
                runs.Add(cur);
            }

            if (cur.Path.Count == 0)
                cur.Path.Add((s.X0, s.Y0));
            else
            {
                var last = cur.Path[^1];
                if (Dist(last, (s.X0, s.Y0)) > 0.25)
                    cur.Path.Add((s.X0, s.Y0));
            }
            if (s.Arc && s.R is double r && r > 1e-6)
            {
                foreach (var pt in OsaiTroyParser.TessellateArc(s.X0, s.Y0, s.X1, s.Y1, r, s.Cw))
                    cur.Path.Add(pt);
            }
            else
                cur.Path.Add((s.X1, s.Y1));
            if (NcCutSim.ArcLengthXy(s) > PointTolMm)
                cur.HasXy = true;
        }
        return runs;
    }

    static CutOp? Classify(CutRun run, int index, double th, double safeZ)
    {
        DedupInPlace(run.Path);
        if (run.Path.Count == 0)
            return null;

        var toolId = "T" + run.ToolNum;
        var z = run.WorkZ;
        var through = z <= ThroughHi;
        var firstLeave = z >= FirstLeaveLo && z <= FirstLeaveHi;
        var blind = z >= BlindLo && z < safeZ - 8;
        var closed = IsClosed(run.Path);
        var depth = DepthFromZ(z, th, through);

        if (!run.HasXy || run.Path.Count < 2)
        {
            var pt = run.Path[0];
            return new CutOp
            {
                Op = "drill",
                PanelId = "NC",
                FeatureId = "H" + index,
                ToolId = toolId,
                Placed = true,
                SheetX = pt.X,
                SheetY = pt.Y,
                X = pt.X,
                Y = pt.Y,
                DiameterMm = DiameterOf(run.ToolNum),
                DepthMm = depth,
                ThicknessMm = th,
                Through = through,
            };
        }

        if (run.ToolNum == 3 && PathLength(run.Path) < 2)
        {
            var pt = Centroid(run.Path);
            return new CutOp
            {
                Op = "drill",
                PanelId = "NC",
                FeatureId = "H" + index,
                ToolId = toolId,
                Placed = true,
                SheetX = pt.X,
                SheetY = pt.Y,
                X = pt.X,
                Y = pt.Y,
                DiameterMm = DiameterOf(3),
                DepthMm = depth,
                ThicknessMm = th,
                Through = through,
            };
        }

        var path = StripRamp(run.Path, z);
        closed = IsClosed(path);
        if (closed && path.Count >= 4 && (through || firstLeave))
        {
            return new CutOp
            {
                Op = "contour",
                PanelId = "NC",
                ToolId = toolId,
                Placed = true,
                ClosePath = true,
                Through = through,
                ThicknessMm = th,
                DepthMm = through ? CamSafety.OuterContourDepthMm(th) : depth,
                Path = path,
            };
        }

        if (run.ToolNum == 1)
        {
            return new CutOp
            {
                Op = "groove",
                PanelId = "NC",
                FeatureId = "TG" + index,
                ToolId = toolId,
                Placed = true,
                IsTongue = true,
                ClosePath = false,
                DepthMm = depth,
                ThicknessMm = th,
                Through = through,
                Path = path,
            };
        }

        if (closed && path.Count >= 4 && blind)
        {
            return new CutOp
            {
                Op = "pocket",
                PanelId = "NC",
                FeatureId = "PK" + index,
                ToolId = toolId,
                Placed = true,
                ClosePath = true,
                DepthMm = depth,
                ThicknessMm = th,
                Through = false,
                Path = path,
                FinishLoop = path,
            };
        }

        if (LooksGuillotine(path, through))
        {
            return new CutOp
            {
                Op = "remnant",
                PanelId = "NC",
                FeatureId = "guillotine",
                ToolId = toolId,
                Placed = true,
                ClosePath = false,
                Through = true,
                ThicknessMm = th,
                DepthMm = CamSafety.OuterContourDepthMm(th),
                Path = path,
            };
        }

        return new CutOp
        {
            Op = "groove",
            PanelId = "NC",
            FeatureId = "G" + index,
            ToolId = toolId,
            Placed = true,
            ClosePath = false,
            DepthMm = depth,
            ThicknessMm = th,
            Through = through,
            Path = path,
        };
    }

    /// <summary>
    /// The Troy post cuts every closed profile in several passes (leave skin, then through),
    /// and since the passes are re-ordered for travel each pass may enter the loop at a
    /// different vertex or run the other way round. All passes over one loop collapse into
    /// the single deepest op so a panel is recovered once, not once per pass.
    /// </summary>
    static List<CutOp> MergeTwoPassContours(List<CutOp> ops)
    {
        var contours = ops.Where(o => o.Op == "contour").ToList();
        var others = ops.Where(o => o.Op != "contour").ToList();
        var used = new bool[contours.Count];
        var merged = new List<CutOp>();
        for (var i = 0; i < contours.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var best = contours[i];
            var passes = 1;
            for (var j = i + 1; j < contours.Count; j++)
            {
                if (used[j]) continue;
                if (!SameLoop(best.Path, contours[j].Path)) continue;
                used[j] = true;
                passes++;
                var other = contours[j];
                if (Deeper(other, best))
                    best = other;
            }
            merged.Add(passes > 1 ? best with { Through = true } : best);
        }
        others.AddRange(merged);
        return others;
    }

    static bool Deeper(CutOp candidate, CutOp current)
    {
        if (candidate.Through != current.Through)
            return candidate.Through;
        return (candidate.DepthMm ?? 0) > (current.DepthMm ?? 0);
    }

    const double LoopCentroidTolMm = 3;
    const double LoopAreaTolRatio = 0.06;
    const double LoopLengthTolRatio = 0.08;

    /// <summary>
    /// Start-vertex, direction and tessellation independent loop identity: compares the
    /// area centroid, the enclosed area and the perimeter instead of the raw vertex list.
    /// </summary>
    public static bool SameLoop(
        IReadOnlyList<(double X, double Y)>? a,
        IReadOnlyList<(double X, double Y)>? b)
    {
        if (a is not { Count: >= 3 } || b is not { Count: >= 3 }) return false;
        var la = LoopOf(a);
        var lb = LoopOf(b);
        var (ca, areaA) = AreaCentroid(la);
        var (cb, areaB) = AreaCentroid(lb);
        if (Dist(ca, cb) > LoopCentroidTolMm) return false;
        var areaTol = Math.Max(25, Math.Max(areaA, areaB) * LoopAreaTolRatio);
        if (Math.Abs(areaA - areaB) > areaTol) return false;
        var lenA = PathLength(la) + Dist(la[^1], la[0]);
        var lenB = PathLength(lb) + Dist(lb[^1], lb[0]);
        return Math.Abs(lenA - lenB) < Math.Max(8, Math.Max(lenA, lenB) * LoopLengthTolRatio);
    }

    /// <summary>Drop the explicit closing vertex so the loop is one point per corner.</summary>
    static IReadOnlyList<(double X, double Y)> LoopOf(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count >= 4 && Dist(path[0], path[^1]) <= ClosedTolMm)
            return path.Take(path.Count - 1).ToList();
        return path;
    }

    /// <summary>Shoelace centroid and absolute area; falls back to the vertex mean for slivers.</summary>
    static ((double X, double Y) Centroid, double Area) AreaCentroid(IReadOnlyList<(double X, double Y)> loop)
    {
        var twiceArea = 0d;
        var cx = 0d;
        var cy = 0d;
        for (var i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            twiceArea += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(twiceArea) < 1e-6)
            return (Centroid(loop), 0);
        var k = 1 / (3 * twiceArea);
        return ((cx * k, cy * k), Math.Abs(twiceArea) * 0.5);
    }

    static List<(double X, double Y)> StripRamp(List<(double X, double Y)> path, double cutZ)
    {
        if (path.Count < 4) return path;
        // First chord is often the 45° ramp (long XY while arriving at cut Z).
        // Never peel a real side off a closed loop: small windows and travel-optimised
        // entry corners have sides in the ramp range, and an opened loop is no longer a
        // contour, so the hole (or the whole panel) would be classified as a groove.
        var d0 = Dist(path[0], path[1]);
        if (d0 > 8 && d0 < 80)
        {
            var skipped = path.Skip(1).ToList();
            if (!IsClosed(path) || IsClosed(skipped))
                return skipped;
        }
        _ = cutZ;
        return path;
    }

    static bool LooksGuillotine(IReadOnlyList<(double X, double Y)> path, bool through)
    {
        if (!through || path.Count is < 2 or > 8) return false;
        var w = path.Max(p => p.X) - path.Min(p => p.X);
        var h = path.Max(p => p.Y) - path.Min(p => p.Y);
        return (w >= 400 && h < 40) || (h >= 400 && w < 40);
    }

    static bool IsClosed(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 3) return false;
        return Dist(path[0], path[^1]) <= ClosedTolMm;
    }

    static double DepthFromZ(double z, double th, bool through)
    {
        if (through) return CamSafety.OuterContourDepthMm(th);
        if (z >= BlindLo) return Math.Max(0.1, th - z);
        if (z >= FirstLeaveLo && z <= FirstLeaveHi) return Math.Max(0.1, th - z);
        return Math.Abs(z);
    }

    static double DiameterOf(int toolNum) =>
        ToolCatalog.DefaultMap().TryGetValue("T" + toolNum, out var t) && t.DiameterMm > 0
            ? t.DiameterMm
            : toolNum == 3 ? 3 : toolNum == 1 ? 6.35 : 10;

    static void DedupInPlace(List<(double X, double Y)> path)
    {
        for (var i = path.Count - 1; i >= 1; i--)
        {
            if (Dist(path[i], path[i - 1]) < 1e-4)
                path.RemoveAt(i);
        }
    }

    static (double X, double Y) Centroid(IReadOnlyList<(double X, double Y)> path)
    {
        var x = 0d;
        var y = 0d;
        foreach (var p in path)
        {
            x += p.X;
            y += p.Y;
        }
        return (x / path.Count, y / path.Count);
    }

    static double PathLength(IReadOnlyList<(double X, double Y)> path)
    {
        var len = 0d;
        for (var i = 1; i < path.Count; i++)
            len += Dist(path[i - 1], path[i]);
        return len;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
