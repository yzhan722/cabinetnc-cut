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

    static List<CutOp> MergeTwoPassContours(List<CutOp> ops)
    {
        var contours = ops.Where(o => o.Op == "contour").ToList();
        var others = ops.Where(o => o.Op != "contour").ToList();
        var used = new bool[contours.Count];
        var merged = new List<CutOp>();
        for (var i = 0; i < contours.Count; i++)
        {
            if (used[i]) continue;
            var a = contours[i];
            var partner = -1;
            for (var j = i + 1; j < contours.Count; j++)
            {
                if (used[j]) continue;
                if (SameLoop(a.Path, contours[j].Path))
                {
                    partner = j;
                    break;
                }
            }
            if (partner >= 0)
            {
                used[partner] = true;
                var last = a.Through || (a.DepthMm ?? 0) >= (contours[partner].DepthMm ?? 0)
                    ? a : contours[partner];
                merged.Add(last with { Through = true });
            }
            else
                merged.Add(a);
            used[i] = true;
        }
        others.AddRange(merged);
        return others;
    }

    static bool SameLoop(
        IReadOnlyList<(double X, double Y)>? a,
        IReadOnlyList<(double X, double Y)>? b)
    {
        if (a is not { Count: >= 3 } || b is not { Count: >= 3 }) return false;
        var ca = Centroid(a);
        var cb = Centroid(b);
        if (Dist(ca, cb) > 3) return false;
        return Math.Abs(PathLength(a) - PathLength(b)) < Math.Max(8, PathLength(a) * 0.08);
    }

    static List<(double X, double Y)> StripRamp(List<(double X, double Y)> path, double cutZ)
    {
        if (path.Count < 4) return path;
        // First chord is often the 45° ramp (long XY while arriving at cut Z).
        var d0 = Dist(path[0], path[1]);
        if (d0 > 8 && d0 < 80)
            return path.Skip(1).ToList();
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
