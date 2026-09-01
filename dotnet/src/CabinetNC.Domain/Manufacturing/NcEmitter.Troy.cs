namespace CabinetNC.Domain.Manufacturing;

using System.Globalization;
using System.Text;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;

public static partial class NcEmitter
{
    /// <summary>OSAI-Troy.con: N-words, (UAO,1), M6 T, G79, 4-dec XYZ, F 1-dec, M30.</summary>
    static string EmitTroy(
        List<CutOp> list,
        MachineProfile profile,
        PostRecipe recipe,
        IReadOnlyDictionary<string, ToolDefinition> catalog)
    {
        _ = catalog;
        _ = profile;
        var contours = list.Where(o => o.Op == "contour" && o.Path is { Count: >= 3 }).ToList();
        var pockets = list.Where(o => o.Op == "pocket" && (
            o.PathSegments is { Count: > 0 } || o.Path is { Count: >= 2 })).ToList();
        var drills = list.Where(o => o.Op == "drill" && o.SheetX is not null).ToList();
        var grooves = list.Where(o => o.Op == "groove" && o.Path is { Count: >= 2 }).ToList();
        var remnants = list.Where(o => o.Op == "remnant" && o.Path is { Count: >= 2 }).ToList();
        var all = CamSafety.OrderSafe(contours.Concat(pockets).Concat(drills).Concat(grooves).Concat(remnants)).ToList();
        if (recipe.Bridges.Count > 0)
        {
            var toolDia = ClearanceToolPick.DiameterOf(TroyRecipe.WorkToolId);
            recipe = recipe.WithBridges(
                ProfileBridgePlanner.EnsureFacingPairs(
                    recipe.Bridges, all, null, toolDia));
        }

        var w = new OsaiTroyWriter(recipe.HomeXyAtEnd);
        var rpm = recipe.ProfileFirstRpm > 0 ? recipe.ProfileFirstRpm : TroyRecipe.SpindleRpm;
        var firstTool = FirstToolNum(all);
        w.StartProgram(firstTool, rpm);

        foreach (var group in all.GroupBy(o => o.SheetIndex).OrderBy(g => g.Key))
        {
            var ordered = CamSafety.OrderSafe(group).ToList();
            var atX = 0d;
            var atY = 0d;

            foreach (var d in ordered.Where(o => o.Op == "drill"))
            {
                w.ToolChange(ToolNum(d.ToolId), recipe.DrillRpm > 0 ? recipe.DrillRpm : rpm);
                EmitTroyDrill(w, d, recipe);
                if (d.SheetX is double sx && d.SheetY is double sy)
                    (atX, atY) = (sx, sy);
            }

            foreach (var g in ordered.Where(o => o.Op == "groove" && o.IsTongue))
            {
                w.ToolChange(ToolNum(g.ToolId), recipe.TongueRpm > 0 ? recipe.TongueRpm : rpm);
                EmitTroyGroove(w, g, recipe, recipe.TongueFeed, recipe.TonguePlunge);
                (atX, atY) = LastXy(g, atX, atY);
            }

            foreach (var c in ordered.Where(o => o.Op == "pocket"
                         || (o.Op == "groove" && !o.IsTongue)))
            {
                w.ToolChange(ToolNum(c.ToolId), recipe.ClearanceRpm > 0 ? recipe.ClearanceRpm : rpm);
                if (c.Op == "pocket")
                    EmitTroyPocket(w, c, recipe);
                else
                    EmitTroyGroove(w, c, recipe, recipe.ClearanceFeed, recipe.ClearancePlunge);
                (atX, atY) = LastXy(c, atX, atY);
            }

            var profileOps = ordered.Where(o => o.Op == "contour").ToList();
            var innerProfiles = profileOps
                .Where(o => !string.IsNullOrWhiteSpace(o.FeatureId))
                .ToList();
            var outerProfiles = profileOps
                .Where(o => string.IsNullOrWhiteSpace(o.FeatureId))
                .ToList();
            // One leave pass for every window + outer, then one through pass.
            EmitTroyProfilePass(w, innerProfiles, recipe, rpm, ref atX, ref atY, lastPass: false, optimize: false);
            EmitTroyProfilePass(w, outerProfiles, recipe, rpm, ref atX, ref atY, lastPass: false, optimize: true);
            EmitTroyProfilePass(w, innerProfiles, recipe, rpm, ref atX, ref atY, lastPass: true, optimize: false);
            EmitTroyProfilePass(w, outerProfiles, recipe, rpm, ref atX, ref atY, lastPass: true, optimize: true);

            foreach (var r in ordered.Where(o => o.Op == "remnant"))
            {
                w.ToolChange(ToolNum(r.ToolId), recipe.ProfileLastRpm > 0 ? recipe.ProfileLastRpm : rpm);
                EmitTroyGuillotine(w, r, recipe);
                (atX, atY) = LastXy(r, atX, atY);
            }
        }

        w.EndProgram();
        return w.Text();
    }

    static int FirstToolNum(IReadOnlyList<CutOp> ops)
    {
        foreach (var o in ops.Where(o => o.Op == "drill"))
            return ToolNum(o.ToolId);
        foreach (var o in ops.Where(o => o.Op == "groove" && o.IsTongue))
            return ToolNum(o.ToolId);
        foreach (var o in ops.Where(o => o.Op == "pocket" || (o.Op == "groove" && !o.IsTongue)))
            return ToolNum(o.ToolId);
        foreach (var o in ops.Where(o => o.Op == "contour"))
            return ToolNum(o.ToolId);
        foreach (var o in ops.Where(o => o.Op == "remnant"))
            return ToolNum(o.ToolId);
        return 2;
    }

    static int ToolNum(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId)) return 2;
        var s = toolId.Trim();
        if (s.Length >= 2 && (s[0] is 'T' or 't') && int.TryParse(s.AsSpan(1), out var n) && n > 0)
            return n;
        return int.TryParse(s, out var raw) && raw > 0 ? raw : 2;
    }

    static void EmitTroyDrill(OsaiTroyWriter w, CutOp d, PostRecipe recipe)
    {
        var z = DrillWorkZ(d, recipe);
        w.Rapid(d.SheetX, d.SheetY, recipe.SafeZMm);
        w.Feed(null, null, z, recipe.DrillPlunge);
        w.Rapid(null, null, recipe.SafeZMm);
    }

    static void EmitTroyGroove(
        OsaiTroyWriter w, CutOp g, PostRecipe recipe, double feed, double feedZ)
    {
        if (g.PocketTooSmallForTool)
            return;
        var z = FeatureWorkZ(g, recipe);
        var segments = g.PathSegments;
        if (segments is { Count: > 0 } || g.FinishLoop is { Count: >= 3 })
        {
            (double X, double Y)? lastCutPoint = null;
            if (segments is { Count: > 0 })
            {
                foreach (var seg in segments)
                {
                    if (seg.Count < 2) continue;
                    ApproachOrLinkAtDepth(w, lastCutPoint, seg[0], z, feed, feedZ, recipe.SafeZMm);
                    EmitFittedXy(w, seg, feed, closed: false);
                    lastCutPoint = seg[^1];
                }
            }
            if (g.FinishLoop is { Count: >= 3 } finish)
            {
                ApproachOrLinkAtDepth(w, lastCutPoint, finish[0], z, feed, feedZ, recipe.SafeZMm);
                EmitFittedXy(w, finish, feed, closed: true);
            }
            w.Rapid(null, null, recipe.SafeZMm);
            return;
        }

        if (g.Path is not { Count: >= 2 } path)
            return;
        w.Rapid(path[0].X, path[0].Y, recipe.SafeZMm);
        w.Feed(null, null, z, feedZ);
        EmitFittedXy(w, path, feed, closed: false);
        w.Rapid(null, null, recipe.SafeZMm);
    }

    static void EmitTroyPocket(OsaiTroyWriter w, CutOp c, PostRecipe recipe)
    {
        if (c.DepthMm is null or <= 0 || c.PocketTooSmallForTool)
            return;
        var z = FeatureWorkZ(c, recipe);
        var feed = recipe.ClearanceFeed;
        var feedZ = recipe.ClearancePlunge;
        var segments = c.PathSegments;
        if (segments is null || segments.Count == 0)
        {
            if (c.Path is not { Count: >= 2 }) return;
            w.Rapid(c.Path[0].X, c.Path[0].Y, recipe.SafeZMm);
            w.Feed(null, null, z, feedZ);
            EmitFittedXy(w, c.Path, feed, c.ClosePath);
            w.Rapid(null, null, recipe.SafeZMm);
            return;
        }

        (double X, double Y)? lastCutPoint = null;
        IReadOnlyList<(double X, double Y)>? firstSeg = null;
        foreach (var seg in segments)
        {
            if (seg.Count < 2) continue;
            firstSeg ??= seg;
            ApproachOrLinkAtDepth(w, lastCutPoint, seg[0], z, feed, feedZ, recipe.SafeZMm);
            EmitFittedXy(w, seg, feed, closed: IsClosedLoop(seg));
            lastCutPoint = seg[^1];
        }
        if (c.FinishLoop is { Count: >= 3 } finish && !SameLoop(finish, firstSeg))
        {
            ApproachOrLinkAtDepth(w, lastCutPoint, finish[0], z, feed, feedZ, recipe.SafeZMm);
            EmitFittedXy(w, finish, feed, closed: true);
        }
        w.Rapid(null, null, recipe.SafeZMm);
    }

    static void EmitTroyProfilePass(
        OsaiTroyWriter w,
        IReadOnlyList<CutOp> ops,
        PostRecipe recipe,
        double rpm,
        ref double atX,
        ref double atY,
        bool lastPass,
        bool optimize)
    {
        if (ops.Count == 0) return;
        var ordered = !optimize
            ? ops
            : lastPass
                ? OuterProfileOrder.Safest(ops, atX, atY)
                : OuterProfileOrder.Fastest(ops, atX, atY);
        var passRpm = lastPass
            ? (recipe.ProfileLastRpm > 0 ? recipe.ProfileLastRpm : rpm)
            : (recipe.ProfileFirstRpm > 0 ? recipe.ProfileFirstRpm : rpm);
        foreach (var c in ordered)
        {
            w.ToolChange(ToolNum(c.ToolId), passRpm);
            (atX, atY) = EmitTroyProfile(w, c, recipe, lastPass, atX, atY, pickEntry: optimize);
        }
    }

    static (double X, double Y) LastXy(CutOp o, double fallbackX, double fallbackY)
    {
        if (o.FinishLoop is { Count: > 0 } fin)
            return fin[^1];
        if (o.PathSegments is { Count: > 0 } segs)
        {
            for (var i = segs.Count - 1; i >= 0; i--)
            {
                if (segs[i].Count >= 1)
                    return segs[i][^1];
            }
        }

        if (o.Path is { Count: > 0 } path)
            return path[^1];
        if (o.SheetX is double sx && o.SheetY is double sy)
            return (sx, sy);
        return (fallbackX, fallbackY);
    }

    static void EmitTroyGuillotine(OsaiTroyWriter w, CutOp r, PostRecipe recipe)
    {
        var path = r.Path!;
        var z = recipe.GuillotineThroughZMm;
        var feed = recipe.GuillotineFeed > 0 ? recipe.GuillotineFeed : TroyRecipe.GuillotineFeedMmMin;
        var feedZ = recipe.GuillotinePlunge > 0 ? recipe.GuillotinePlunge : TroyRecipe.GuillotinePlungeMmMin;
        w.Rapid(path[0].X, path[0].Y, recipe.SafeZMm);
        w.Feed(null, null, z, feedZ);
        EmitFittedXy(w, path, feed, closed: false);
        w.Rapid(null, null, recipe.SafeZMm);
    }

    static (double X, double Y) EmitTroyProfile(
        OsaiTroyWriter w, CutOp c, PostRecipe recipe, bool lastPass,
        double fromX, double fromY, bool pickEntry)
    {
        var path = c.Path!;
        var safeZ = recipe.SafeZMm;
        var cutZ = lastPass ? recipe.ProfileThroughZMm : recipe.ProfileFirstLeaveMm;
        var feed = lastPass ? recipe.ProfileLastFeed : recipe.ProfileFirstFeed;
        var feedZ = lastPass ? recipe.ProfileLastPlunge : recipe.ProfileFirstPlunge;
        var entryArc = pickEntry ? OuterProfileOrder.EntryArc(path, fromX, fromY) : 0;
        var entry = PolylineQuery.PointAtArc(path, entryArc, c.ClosePath) ?? path[0];
        w.Rapid(entry.X, entry.Y, safeZ);

        var startArc = entryArc;
        if (!lastPass && recipe.ProfileFirstRamp45)
            startArc = EmitRamp45(w, path, c.ClosePath, safeZ, cutZ, feedZ, entryArc);
        else
            w.Feed(null, null, cutZ, feedZ);

        // First leave pass cuts the whole loop at 0.5 — that skin is the tab.
        // Last through pass still lifts over the web so the 0.5 mm stays.
        var bridges = lastPass
            ? recipe.Bridges.Where(b =>
                b.SheetIndex == c.SheetIndex
                && string.Equals(b.PanelId, c.PanelId, StringComparison.Ordinal)
                && string.Equals(b.FeatureId ?? "", c.FeatureId ?? "", StringComparison.Ordinal))
            : [];
        var bridgeZ = Math.Max(cutZ, recipe.ProfileFirstLeaveMm);
        var toolDia = ClearanceToolPick.DiameterOf(c.ToolId ?? TroyRecipe.WorkToolId);

        EmitPathFromArc(
            w, path, c.ClosePath, startArc, cutZ, bridgeZ, feed, feedZ,
            bridges, toolDia, lastPass, safeZ, c.CadPath);
        w.Rapid(null, null, safeZ);
        return entry;
    }

    static double EmitRamp45(
        OsaiTroyWriter w,
        IReadOnlyList<(double X, double Y)> path,
        bool closed,
        double safeZ,
        double cutZ,
        double feedZ,
        double startArc = 0)
    {
        var dz = Math.Abs(safeZ - cutZ);
        var total = PolylineQuery.Length(path, closed);
        if (dz < 1e-6 || total < 1e-6)
        {
            w.Feed(null, null, cutZ, feedZ);
            return startArc;
        }
        var along = Math.Min(dz, total);
        var pt = PolylineQuery.PointAtArc(path, startArc + along, closed);
        if (pt is null)
        {
            w.Feed(null, null, cutZ, feedZ);
            return startArc;
        }
        w.Feed(pt.Value.X, pt.Value.Y, cutZ, feedZ);
        return startArc + along;
    }

    static void EmitPathFromArc(
        OsaiTroyWriter w,
        IReadOnlyList<(double X, double Y)> path,
        bool closed,
        double startArc,
        double cutZ,
        double leaveZ,
        double feed,
        double feedZ,
        IEnumerable<ProfileBridge> bridges,
        double toolDiameterMm,
        bool lastPass,
        double safeZ,
        IReadOnlyList<CadSegment>? cadPath = null)
    {
        var total = PolylineQuery.Length(path, closed);
        if (total < 1e-9) return;
        var gaps = MergeGaps(BridgeGaps(bridges, total, closed, toolDiameterMm), total);
        var arcs = WalkSampleArcs(path, closed, startArc, total, gaps);
        if (arcs.Count == 0) return;

        var useCad = cadPath is { Count: > 0 };
        var cadTotal = useCad ? Geometry.CadPath.Length(cadPath!, closed) : 0;
        var run = new List<(double X, double Y)>();
        var runA0 = 0d;
        var runA1 = 0d;
        var haveRunArc = false;
        void FlushCut()
        {
            if (useCad && haveRunArc && cadTotal > 1e-9)
            {
                var slice = Geometry.CadPath.Slice(cadPath!, runA0, runA1, closed);
                if (slice.Count > 0)
                {
                    EmitCadXy(w, slice, feed);
                    run.Clear();
                    haveRunArc = false;
                    return;
                }
            }
            if (run.Count < 2)
            {
                run.Clear();
                haveRunArc = false;
                return;
            }
            var loop = run.Count >= 4
                && Math.Sqrt(
                    (run[0].X - run[^1].X) * (run[0].X - run[^1].X)
                    + (run[0].Y - run[^1].Y) * (run[0].Y - run[^1].Y)) < 0.05;
            EmitFittedXy(w, run, feed, closed: loop);
            run.Clear();
            haveRunArc = false;
        }

        (double X, double Y)? PtAt(double a)
        {
            var queryArc = a >= total - 1e-9 && a <= total + 1e-9 ? 0 : a;
            return PolylineQuery.PointAtArc(path, queryArc, closed);
        }

        var startPt = PtAt(arcs[0]);
        if (startPt is not null)
        {
            run.Add(startPt.Value);
            runA0 = ScaleArc(arcs[0], total, cadTotal, useCad);
            runA1 = runA0;
            haveRunArc = true;
        }

        var cutting = true;
        for (var i = 1; i < arcs.Count; i++)
        {
            var a0 = arcs[i - 1];
            var a1 = arcs[i];
            var span = a1 >= a0 - 1e-9 ? a1 - a0 : (total - a0) + a1;
            if (span < 1e-6) continue;
            var mid = MidArc(a0, a1, total);
            var inGap = gaps.Any(g => mid >= g.A - 1e-9 && mid <= g.B + 1e-9);
            var pt = PtAt(a1);
            if (pt is null) continue;
            if (inGap)
            {
                if (cutting)
                {
                    FlushCut();
                    if (lastPass)
                    {
                        var at0 = PtAt(a0);
                        if (at0 is not null) run.Add(at0.Value);
                        runA0 = ScaleArc(a0, total, cadTotal, useCad);
                        haveRunArc = true;
                        w.Feed(null, null, leaveZ, feedZ);
                    }
                    else
                    {
                        w.Rapid(null, null, safeZ);
                    }
                    cutting = false;
                }
                if (lastPass)
                {
                    run.Add(pt.Value);
                    runA1 = ScaleArc(a1, total, cadTotal, useCad);
                    haveRunArc = true;
                }
            }
            else
            {
                if (!cutting)
                {
                    FlushCut();
                    var at0 = PtAt(a0);
                    if (!lastPass && at0 is not null)
                        w.Rapid(at0.Value.X, at0.Value.Y, null);
                    w.Feed(null, null, cutZ, feedZ);
                    cutting = true;
                    if (at0 is not null) run.Add(at0.Value);
                    runA0 = ScaleArc(a0, total, cadTotal, useCad);
                    haveRunArc = true;
                }
                run.Add(pt.Value);
                runA1 = ScaleArc(a1, total, cadTotal, useCad);
                haveRunArc = true;
            }
        }
        FlushCut();
    }

    static double ScaleArc(double arc, double polyTotal, double cadTotal, bool useCad) =>
        useCad && polyTotal > 1e-9 ? arc / polyTotal * cadTotal : arc;

    static void EmitCadXy(OsaiTroyWriter w, IReadOnlyList<CadSegment> segs, double feed)
    {
        foreach (var g in segs)
        {
            if (g.IsCircle && g.Center is { } cc && g.RadiusMm > 0)
            {
                var mid = new Point2(
                    2 * cc.X - g.Start.X,
                    2 * cc.Y - g.Start.Y);
                w.Arc(g.Cw, mid.X, mid.Y, g.RadiusMm, feed);
                w.Arc(g.Cw, g.End.X, g.End.Y, g.RadiusMm, feed);
            }
            else if (g.IsArc && g.RadiusMm > 0)
                w.Arc(g.Cw, g.End.X, g.End.Y, g.RadiusMm, feed);
            else
                w.Feed(g.End.X, g.End.Y, null, feed);
        }
    }

    /// <summary>
    /// First entry of a feature: SafeZ rapid + plunge. Same feature, next wall:
    /// stay at cut Z and feed across the floor (Carveco). Different features
    /// are separate ops and still retract at the end of each emit.
    /// </summary>
    static void ApproachOrLinkAtDepth(
        OsaiTroyWriter w,
        (double X, double Y)? lastCutPoint,
        (double X, double Y) next,
        double z,
        double xyFeed,
        double plungeFeed,
        double safeZ)
    {
        if (lastCutPoint is { } last && SamePoint(last, next))
            return;
        if (lastCutPoint is not null)
        {
            w.Feed(next.X, next.Y, z, xyFeed);
            return;
        }

        w.Rapid(null, null, safeZ);
        w.Rapid(next.X, next.Y, safeZ);
        w.Feed(null, null, z, plungeFeed);
    }

    static void EmitFittedXy(
        OsaiTroyWriter w,
        IReadOnlyList<(double X, double Y)> path,
        double feed,
        bool closed)
    {
        foreach (var seg in PolylineArcFit.Fit(path, closed))
        {
            if (seg.Arc)
                w.Arc(seg.Cw, seg.X, seg.Y, seg.R, feed);
            else
                w.Feed(seg.X, seg.Y, null, feed);
        }
    }

    static bool SamePoint((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    static bool IsClosedLoop(IReadOnlyList<(double X, double Y)> path) =>
        path.Count >= 4 && SamePoint(path[0], path[^1]);

    static bool SameLoop(
        IReadOnlyList<(double X, double Y)> a,
        IReadOnlyList<(double X, double Y)>? b)
    {
        if (b is null || a.Count < 3 || b.Count < 3)
            return false;
        var aa = OpenRing(a);
        var bb = OpenRing(b);
        if (aa.Count < 3 || bb.Count < 3 || aa.Count != bb.Count)
            return false;
        var start = -1;
        for (var i = 0; i < bb.Count; i++)
        {
            if (!SamePoint(aa[0], bb[i])) continue;
            start = i;
            break;
        }
        if (start < 0) return false;
        for (var i = 0; i < aa.Count; i++)
        {
            if (!SamePoint(aa[i], bb[(start + i) % bb.Count]))
                return false;
        }
        return true;
    }

    static List<(double X, double Y)> OpenRing(IReadOnlyList<(double X, double Y)> loop)
    {
        var pts = loop.ToList();
        if (pts.Count >= 2 && SamePoint(pts[0], pts[^1]))
            pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    static List<double> WalkSampleArcs(
        IReadOnlyList<(double X, double Y)> path,
        bool closed,
        double startArc,
        double total,
        IReadOnlyList<(double A, double B)> gaps)
    {
        var marks = new List<double>();
        void Mark(double a)
        {
            if (a < -1e-9 || a > total + 1e-9)
                a = WrapArc(a, total, closed);
            else
                a = Math.Clamp(a, 0, total);
            if (marks.All(m => Math.Abs(m - a) > 1e-6))
                marks.Add(a);
        }
        foreach (var v in VertexArcs(path, closed))
            Mark(v);
        foreach (var g in gaps)
        {
            Mark(g.A);
            Mark(g.B);
        }
        Mark(startArc);
        if (marks.All(m => Math.Abs(m - total) > 1e-6))
            marks.Add(total);
        marks.Sort();

        var walk = new List<double>();
        void Add(double a)
        {
            if (walk.Count == 0 || Math.Abs(walk[^1] - a) > 1e-9)
                walk.Add(a);
        }

        foreach (var a in marks.Where(a => a >= startArc - 1e-9))
            Add(a);
        if (closed && startArc > 1e-6)
        {
            foreach (var a in marks.Where(a => a <= startArc + 1e-9))
                Add(a);
        }
        return walk;
    }

    static double MidArc(double a0, double a1, double total)
    {
        if (a1 >= a0 - 1e-9)
            return (a0 + a1) / 2;
        var len = (total - a0) + a1;
        var mid = a0 + len / 2;
        return mid >= total ? mid - total : mid;
    }

    static List<double> VertexArcs(IReadOnlyList<(double X, double Y)> path, bool closed)
    {
        var arcs = new List<double> { 0 };
        double walked = 0;
        var n = closed ? path.Count : path.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = path[i];
            var b = path[(i + 1) % path.Count];
            walked += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            arcs.Add(walked);
        }
        return arcs;
    }

    static List<(double A, double B)> BridgeGaps(
        IEnumerable<ProfileBridge> bridges, double total, bool closed, double toolDiameterMm)
    {
        var gaps = new List<(double A, double B)>();
        foreach (var b in bridges)
        {
            var half = Math.Max(0.2, ProfileBridgePlanner.ToolCenterSpanMm(b.WidthMm, toolDiameterMm) / 2);
            var a = b.ArcLengthMm - half;
            var c = b.ArcLengthMm + half;
            if (!closed)
            {
                gaps.Add((Math.Max(0, a), Math.Min(total, c)));
                continue;
            }
            a = WrapArc(a, total, true);
            c = WrapArc(c, total, true);
            if (c >= a)
                gaps.Add((a, c));
            else
            {
                gaps.Add((a, total));
                gaps.Add((0, c));
            }
        }
        return gaps;
    }

    static List<(double A, double B)> MergeGaps(List<(double A, double B)> gaps, double total)
    {
        var list = gaps.Where(g => g.B - g.A > 1e-4).OrderBy(g => g.A).ToList();
        if (list.Count == 0) return list;
        var merged = new List<(double A, double B)> { list[0] };
        for (var i = 1; i < list.Count; i++)
        {
            var last = merged[^1];
            var next = list[i];
            if (next.A <= last.B + 1e-4)
                merged[^1] = (last.A, Math.Max(last.B, next.B));
            else
                merged.Add(next);
        }
        return merged.Select(g => (Math.Max(0, g.A), Math.Min(total, g.B))).ToList();
    }

    static double WrapArc(double arc, double total, bool closed)
    {
        if (!closed || total < 1e-9) return Math.Clamp(arc, 0, total);
        var t = arc % total;
        if (t < 0) t += total;
        return t;
    }

    static double FeatureWorkZ(CutOp op, PostRecipe recipe)
    {
        var depth = Math.Abs(op.DepthMm ?? 0);
        if (op.Through)
            return recipe.ProfileThroughZMm;
        if (recipe.Z0IsBoardBottom && op.ThicknessMm is > 0)
            return op.ThicknessMm.Value - depth;
        return -depth;
    }

    static double DrillWorkZ(CutOp d, PostRecipe recipe)
    {
        var depth = Math.Abs(d.DepthMm ?? 0);
        var th = d.ThicknessMm ?? 0;
        var through = d.Through || (th > 0 && depth >= th - 0.05);
        if (through)
            return recipe.DrillThroughZMm;
        if (recipe.Z0IsBoardBottom && th > 0)
            return th - depth;
        return -depth;
    }

    sealed class OsaiTroyWriter
    {
        readonly List<string> _lines = [];
        readonly bool _homeXyAtEnd;
        int _n = 1;
        double? _x, _y, _z, _f;
        public int? Tool { get; private set; }

        const double Eps = 5e-5;

        public OsaiTroyWriter(bool homeXyAtEnd = true) => _homeXyAtEnd = homeXyAtEnd;

        public string Text() => string.Join("\r\n", _lines) + "\r\n";

        void Line(string body)
        {
            _lines.Add("N" + _n.ToString(CultureInfo.InvariantCulture) + " " + body);
            _n++;
        }

        public void StartProgram(int tool, double rpm)
        {
            Line("G90 ");
            Line("G40 ");
            Line("G80 ");
            Line("(UAO,1)");
            Line("G79 Z0");
            Line("M05");
            Line("M52");
            Line("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            Line("M3 S" + Math.Round(rpm).ToString("0", CultureInfo.InvariantCulture));
            Line("(DLY,3)");
            Line("M49");
            Line("G27");
            Line("G17");
            Tool = tool;
            Rapid(0, 0, null);
        }

        public void ToolChange(int tool, double rpm)
        {
            if (Tool == tool) return;
            Line("M5");
            Line("M52");
            Line("M6 T" + tool.ToString(CultureInfo.InvariantCulture));
            Line("M3 S" + Math.Round(rpm).ToString("0", CultureInfo.InvariantCulture));
            Line("(DLY,3)");
            Line("M49");
            Line("G27");
            Line("G17");
            _x = _y = _z = _f = null;
            Tool = tool;
        }

        public void EndProgram()
        {
            if (_homeXyAtEnd)
            {
                Line("G0 X" + Xyz(0) + " Y" + Xyz(0));
                _x = 0;
                _y = 0;
            }
            Line("G80");
            Line("M5");
            Line("G79 Z0");
            Line("M30");
        }

        static string Xyz(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
        static string FeedFmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        static bool Same(double? last, double next) =>
            last is double l && Math.Abs(l - next) < Eps;

        public void Rapid(double? x, double? y, double? z)
        {
            var sb = new StringBuilder("G0");
            if (x is double xv && !Same(_x, xv)) { sb.Append(" X"); sb.Append(Xyz(xv)); _x = xv; }
            if (y is double yv && !Same(_y, yv)) { sb.Append(" Y"); sb.Append(Xyz(yv)); _y = yv; }
            if (z is double zv && !Same(_z, zv)) { sb.Append(" Z"); sb.Append(Xyz(zv)); _z = zv; }
            if (sb.Length == 2) return;
            Line(sb.ToString());
        }

        public void Feed(double? x, double? y, double? z, double? f)
        {
            var sb = new StringBuilder("G1");
            if (x is double xv && !Same(_x, xv)) { sb.Append(" X"); sb.Append(Xyz(xv)); _x = xv; }
            if (y is double yv && !Same(_y, yv)) { sb.Append(" Y"); sb.Append(Xyz(yv)); _y = yv; }
            if (z is double zv && !Same(_z, zv)) { sb.Append(" Z"); sb.Append(Xyz(zv)); _z = zv; }
            if (f is double fv && !Same(_f, fv)) { sb.Append(" F"); sb.Append(FeedFmt(fv)); _f = fv; }
            if (sb.Length == 2) return;
            Line(sb.ToString());
        }

        public void Arc(bool cw, double x, double y, double r, double? f)
        {
            var sb = new StringBuilder(cw ? "G2" : "G3");
            sb.Append(" X"); sb.Append(Xyz(x));
            sb.Append(" Y"); sb.Append(Xyz(y));
            sb.Append(" R"); sb.Append(Xyz(r));
            if (f is double fv && !Same(_f, fv)) { sb.Append(" F"); sb.Append(FeedFmt(fv)); _f = fv; }
            _x = x;
            _y = y;
            Line(sb.ToString());
        }
    }
}
