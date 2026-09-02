namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Nesting;
using Clipper2Lib;

/// <summary>
/// Pocket area clear — Clipper inset + inward offset rings stitched into a spiral
/// (inside-out), then a separate finish loop. Not a horizontal zigzag raster.
/// ASSUMPTION: stepover = 40% tool Ø; finish/onion allowance = 0.5 mm on walls.
/// </summary>
public static class PocketClearer
{
    public const double DefaultOnionSkinMm = 0.5;
    public const double DefaultStepoverRatio = 0.4;
    /// <summary>
    /// Fusion lay-flat sometimes emits a paper-thin edge ribbon (≈0.1 mm) as a pocket.
    /// That is a tessellation leftover, not a shop feature — skip it. Real pockets that
    /// are merely smaller than the tool stay on the hard preflight gate.
    /// </summary>
    public const double ExportSliverMaxShortMm = 1.0;
    const double Scale = 10000;

    public static bool IsExportSliver(IReadOnlyList<(double X, double Y)> outline)
    {
        if (outline.Count < 3) return false;
        var minX = outline.Min(p => p.X);
        var maxX = outline.Max(p => p.X);
        var minY = outline.Min(p => p.Y);
        var maxY = outline.Max(p => p.Y);
        return Math.Min(maxX - minX, maxY - minY) < ExportSliverMaxShortMm;
    }

    /// <summary>
    /// Fusion sometimes writes a second copy of a feature in lay-flat world XY
    /// (tens of metres away). Those must not become toolpaths.
    /// </summary>
    public const double OffPanelPadMm = 80;

    public static bool IsOffPanelArtifact(
        IReadOnlyList<(double X, double Y)> outline,
        LocalBounds panelBounds,
        double padMm = OffPanelPadMm)
    {
        if (outline.Count == 0) return false;
        var minX = outline.Min(p => p.X);
        var maxX = outline.Max(p => p.X);
        var minY = outline.Min(p => p.Y);
        var maxY = outline.Max(p => p.Y);
        return Disjoint(minX, minY, maxX, maxY, panelBounds, padMm);
    }

    public static bool IsOffPanelArtifact(
        double x,
        double y,
        LocalBounds panelBounds,
        double padMm = OffPanelPadMm) =>
        Disjoint(x, y, x, y, panelBounds, padMm);

    static bool Disjoint(
        double minX, double minY, double maxX, double maxY,
        LocalBounds panel, double padMm) =>
        maxX < panel.MinX - padMm
        || minX > panel.MaxX + padMm
        || maxY < panel.MinY - padMm
        || minY > panel.MaxY + padMm;

    public sealed class PocketClearRequest
    {
        public required IReadOnlyList<(double X, double Y)> Outline { get; init; }
        public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Holes { get; init; } = [];
        public double ToolDiameterMm { get; init; } = 6.35;
        public double? StepoverMm { get; init; }
        public double OnionSkinMm { get; init; } = DefaultOnionSkinMm;
        /// <summary>
        /// Emit a separate wall loop after the spiral. Disable when the spiral's
        /// outermost ring already cuts the feature directly to its final size.
        /// </summary>
        public bool EmitFinishLoop { get; init; } = true;
        /// <summary>
        /// Close every clearance ring before stepping outward. Used by hinge
        /// cups so each displayed/machined ring is a complete circle.
        /// </summary>
        public bool CloseClearRings { get; init; }
        /// <summary>
        /// Panel AABB. When the pocket/slot outline opens onto an edge,
        /// toolpaths are extended through that edge (LED channels).
        /// </summary>
        public Nesting.LocalBounds? PanelBounds { get; init; }
    }

    public sealed class PocketClearResult
    {
        public required IReadOnlyList<(double X, double Y)> Path { get; init; }
        /// <summary>Spiral fill as one (or few) polylines. Finish is <see cref="FinishLoop"/>.</summary>
        public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Segments { get; init; } = [];
        public IReadOnlyList<(double X, double Y)>? FinishLoop { get; init; }
        public int PassCount { get; init; }
        public double StepoverMm { get; init; }
        public double InsetMm { get; init; }
        /// <summary>True when inset region cannot fit the tool (no silent skip).</summary>
        public bool TooSmallForTool { get; init; }
    }

    public static PocketClearResult Clear(PocketClearRequest req)
    {
        if (req.Outline.Count < 3)
            return new PocketClearResult { Path = req.Outline, PassCount = 0, StepoverMm = 0, InsetMm = 0 };

        var toolR = Math.Max(0.1, req.ToolDiameterMm / 2);
        var onion = Math.Max(0, req.OnionSkinMm);
        var inset = toolR + onion;
        var step = req.StepoverMm ?? Math.Max(0.5, req.ToolDiameterMm * DefaultStepoverRatio);

        var holes = req.Holes
            .Where(h => h.Count >= 3)
            .Select(ToPath64)
            .ToList();
        if (holes.Count > 0)
            return ClearRing(req.Outline, holes, step, inset, req.EmitFinishLoop);

        var outer = ToPath64(req.Outline);
        var insetPaths = Clipper.InflatePaths(
            new Paths64 { outer },
            -inset * Scale,
            JoinType.Round,
            EndType.Polygon);
        var regions = new Paths64();
        foreach (var p in insetPaths)
        {
            if (IsAreaTiny(p)) continue;
            regions.Add(p);
        }
        if (regions.Count == 0)
        {
            var cx = req.Outline.Average(p => p.X);
            var cy = req.Outline.Average(p => p.Y);
            return new PocketClearResult
            {
                Path = [(cx, cy)],
                Segments = [],
                PassCount = 0,
                StepoverMm = step,
                InsetMm = inset,
                TooSmallForTool = true,
            };
        }

        if (regions.Count > 1)
            return ClearSplitRegions(req, regions, step, inset);

        var region = regions[0];
        EnsureCcw(region); // inner wall climb with M3 = CCW

        var rings = OffsetRings(region, step);
        // One wall: close it (the missing top of a T was the unclosed edge).
        // Several rings: stitch a spiral; FinishLoop is the outer wall.
        var closeWall = req.CloseClearRings || rings.Count == 1;
        var spiral = StitchSpiralInsideOut(rings, closeWall);
        IReadOnlyList<(double X, double Y)>? finish =
            req.EmitFinishLoop && rings.Count > 1
                ? ClosedLoop(region, spiral.Count > 0 ? spiral[^1] : null)
                : null;

        var flat = new List<(double X, double Y)>();
        if (spiral.Count >= 2)
            flat.AddRange(spiral);
        if (finish is not null)
            flat.AddRange(finish);

        IReadOnlyList<IReadOnlyList<(double X, double Y)>> segments =
            spiral.Count >= 2 ? [spiral] : [];

        return SnapOpenEdges(new PocketClearResult
        {
            Path = flat,
            Segments = segments,
            FinishLoop = finish,
            PassCount = Math.Max(1, rings.Count),
            StepoverMm = step,
            InsetMm = inset,
        }, req);
    }

    /// <summary>
    /// A thin T / LED channel can split after inset. Keep every leftover
    /// (Largest used to drop the bar or a stem — the gaps on B3).
    /// </summary>
    static PocketClearResult ClearSplitRegions(
        PocketClearRequest req,
        Paths64 regions,
        double step,
        double inset)
    {
        var segments = new List<IReadOnlyList<(double X, double Y)>>();
        (double X, double Y)? last = null;
        foreach (var path in OrderNear(regions, last))
        {
            EnsureCcw(path);
            var rings = OffsetRings(path, step);
            var spiral = StitchSpiralInsideOut(rings, closeEachRing: true);
            if (spiral.Count < 3) continue;
            segments.Add(spiral);
            last = spiral[^1];
        }
        if (segments.Count == 0)
            return TooSmall(req.Outline, step, inset);

        var flat = new List<(double X, double Y)>();
        foreach (var loop in segments)
        {
            if (flat.Count > 0)
                flat.Add(loop[0]);
            flat.AddRange(loop);
        }
        return SnapOpenEdges(new PocketClearResult
        {
            Path = flat,
            Segments = segments,
            FinishLoop = null,
            PassCount = segments.Count,
            StepoverMm = step,
            InsetMm = inset,
        }, req);
    }

    static PocketClearResult ClearRing(
        IReadOnlyList<(double X, double Y)> outline,
        List<Path64> holes,
        double step,
        double inset,
        bool emitFinish)
    {
        var outer = ToPath64(outline);
        EnsureCcw(outer);
        var outerInset = Clipper.InflatePaths(
            new Paths64 { outer }, -inset * Scale, JoinType.Round, EndType.Polygon);
        if (outerInset.Count == 0 || outerInset[0].Count < 3)
            return TooSmall(outline, step, inset);

        var outerLoop = ToPoints(Largest(outerInset));
        var innerLoops = new List<IReadOnlyList<(double X, double Y)>>();
        foreach (var hole in holes)
        {
            var expanded = Clipper.InflatePaths(
                new Paths64 { hole }, inset * Scale, JoinType.Round, EndType.Polygon);
            if (expanded.Count == 0 || expanded[0].Count < 3)
                return TooSmall(outline, step, inset);
            var loop = ToPoints(Largest(expanded));
            if (RingSpan(loop) >= RingSpan(outerLoop) - 0.5)
                return TooSmall(outline, step, inset);
            innerLoops.Add(loop);
        }

        // Thin rebate (one tool in the band): two shop walls only.
        // Wide pocket with an island: onion-fill the floor, then the same walls.
        // Do not retrace the outer as FinishLoop — that was a third overlapping pass.
        _ = emitFinish;
        var clips = new Paths64();
        foreach (var loop in innerLoops)
            clips.Add(ToPath64(loop));
        var fill = Clipper.Difference(
            new Paths64 { ToPath64(outerLoop) },
            clips,
            FillRule.NonZero);
        fill = FilterTiny(fill);
        var extras = OffsetFillInward(fill, step);
        var segments = new List<IReadOnlyList<(double X, double Y)>>();
        (double X, double Y)? last = null;
        for (var i = extras.Count - 1; i >= 0; i--)
        {
            foreach (var path in OrderNear(extras[i], last))
            {
                var loop = last is { } near
                    ? StartNear(CloseRing(ToPointsCcw(path)), near)
                    : CloseRing(ToPointsCcw(path));
                if (loop.Count < 3) continue;
                segments.Add(loop);
                last = loop[^1];
            }
        }

        var outerSeg = last is { } afterFill
            ? StartNear(CloseRing(outerLoop), afterFill)
            : StartOnLongestStraight(CloseRing(outerLoop));
        segments.Add(outerSeg);
        last = outerSeg[^1];
        foreach (var loop in innerLoops)
        {
            var inner = StartNear(CloseRing(loop), last.Value);
            segments.Add(inner);
            last = inner[^1];
        }

        var flat = new List<(double X, double Y)>();
        foreach (var loop in segments)
        {
            if (flat.Count > 0)
                flat.Add(loop[0]);
            flat.AddRange(loop);
        }

        return new PocketClearResult
        {
            Path = flat,
            Segments = segments,
            FinishLoop = null,
            PassCount = segments.Count,
            StepoverMm = step,
            InsetMm = inset,
        };
    }

    /// <summary>
    /// Shrink a filled region (outers minus holes) by <paramref name="stepMm"/>.
    /// Off-centre islands leave a leftover floor that keeps offsetting as a
    /// simple pocket after the wrap around the island disappears.
    /// </summary>
    static List<Paths64> OffsetFillInward(Paths64 fill, double stepMm)
    {
        var levels = new List<Paths64>();
        var current = fill;
        for (var i = 0; i < 80; i++)
        {
            var next = ShrinkFill(current, stepMm);
            if (next.Count == 0) break;
            levels.Add(next);
            current = next;
        }
        return levels;
    }

    static Paths64 ShrinkFill(Paths64 fill, double stepMm)
    {
        var outers = new Paths64();
        var holes = new Paths64();
        foreach (var p in fill)
        {
            if (p.Count < 3) continue;
            var area = Clipper.Area(p);
            if (Math.Abs(area) < Scale * Scale * 0.5) continue;
            if (area > 0) outers.Add(p);
            else holes.Add(p);
        }
        if (outers.Count == 0) return [];

        var shrunk = FilterTiny(Clipper.InflatePaths(
            outers, -stepMm * Scale, JoinType.Round, EndType.Polygon));
        if (shrunk.Count == 0) return [];
        if (holes.Count == 0) return shrunk;

        var grown = new Paths64();
        foreach (var hole in holes)
        {
            var ccw = new Path64(hole);
            if (Clipper.Area(ccw) < 0)
                ccw.Reverse();
            var exp = Clipper.InflatePaths(
                new Paths64 { ccw }, stepMm * Scale, JoinType.Round, EndType.Polygon);
            if (exp.Count == 0 || exp[0].Count < 3) continue;
            grown.Add(Largest(exp));
        }
        if (grown.Count == 0) return shrunk;
        return FilterTiny(Clipper.Difference(shrunk, grown, FillRule.NonZero));
    }

    const double SliverShortMm = 2.0;
    const double EdgeOvershootMm = 1.5;
    const double EdgeTouchMm = 1.5;

    static bool IsAreaTiny(Path64 path) =>
        path.Count < 3 || Math.Abs(Clipper.Area(path)) < Scale * Scale * 0.5;

    static PocketClearResult SnapOpenEdges(PocketClearResult result, PocketClearRequest req)
    {
        if (req.PanelBounds is not { } panel || result.TooSmallForTool)
            return result;
        var outline = req.Outline;
        if (outline.Count < 3) return result;

        var openL = outline.Min(p => p.X) <= panel.MinX + EdgeTouchMm;
        var openR = outline.Max(p => p.X) >= panel.MaxX - EdgeTouchMm;
        var openB = outline.Min(p => p.Y) <= panel.MinY + EdgeTouchMm;
        var openT = outline.Max(p => p.Y) >= panel.MaxY - EdgeTouchMm;
        if (!openL && !openR && !openB && !openT)
            return result;

        IReadOnlyList<(double X, double Y)> Snap(IReadOnlyList<(double X, double Y)> path) =>
            SnapPathToOpenEdges(path, panel, openL, openR, openB, openT);

        var segs = result.Segments.Select(Snap).ToList();
        var finish = result.FinishLoop is { Count: >= 3 } f ? Snap(f) : result.FinishLoop;
        var flat = new List<(double X, double Y)>();
        foreach (var loop in segs)
        {
            if (flat.Count > 0 && loop.Count > 0)
                flat.Add(loop[0]);
            flat.AddRange(loop);
        }
        if (finish is not null)
            flat.AddRange(finish);
        if (flat.Count < 2 && result.Path.Count >= 2)
            flat = Snap(result.Path).ToList();

        return new PocketClearResult
        {
            Path = flat.Count >= 2 ? flat : result.Path,
            Segments = segs,
            FinishLoop = finish,
            PassCount = result.PassCount,
            StepoverMm = result.StepoverMm,
            InsetMm = result.InsetMm,
            TooSmallForTool = result.TooSmallForTool,
        };
    }

    static IReadOnlyList<(double X, double Y)> SnapPathToOpenEdges(
        IReadOnlyList<(double X, double Y)> path,
        Nesting.LocalBounds panel,
        bool openL, bool openR, bool openB, bool openT)
    {
        if (path.Count == 0) return path;
        var minX = path.Min(p => p.X);
        var maxX = path.Max(p => p.X);
        var minY = path.Min(p => p.Y);
        var maxY = path.Max(p => p.Y);
        const double band = 1.25;
        var pts = new List<(double X, double Y)>(path.Count);
        foreach (var p in path)
        {
            var x = p.X;
            var y = p.Y;
            if (openL && Math.Abs(p.X - minX) <= band)
                x = panel.MinX - EdgeOvershootMm;
            if (openR && Math.Abs(p.X - maxX) <= band)
                x = panel.MaxX + EdgeOvershootMm;
            if (openB && Math.Abs(p.Y - minY) <= band)
                y = panel.MinY - EdgeOvershootMm;
            if (openT && Math.Abs(p.Y - maxY) <= band)
                y = panel.MaxY + EdgeOvershootMm;
            pts.Add((x, y));
        }
        return pts;
    }

    static Paths64 FilterTiny(Paths64 paths)
    {
        var kept = new Paths64();
        foreach (var p in paths)
        {
            if (IsSliverRing(p)) continue;
            kept.Add(p);
        }
        return kept;
    }

    static bool IsSliverRing(Path64 path)
    {
        if (path.Count < 3) return true;
        var area = Math.Abs(Clipper.Area(path)) / (Scale * Scale);
        if (area < 0.5) return true;
        var pts = ToPoints(path);
        if (pts.Count < 3) return true;
        var w = pts.Max(p => p.X) - pts.Min(p => p.X);
        var h = pts.Max(p => p.Y) - pts.Min(p => p.Y);
        if (Math.Min(w, h) < SliverShortMm) return true;
        var peri = 0d;
        for (var i = 0; i < pts.Count; i++)
            peri += Dist(pts[i], pts[(i + 1) % pts.Count]);
        return peri > 1 && 2 * area / peri < 1.25;
    }

    static List<(double X, double Y)> ToPointsCcw(Path64 path)
    {
        var copy = new Path64(path);
        EnsureCcw(copy);
        return ToPoints(copy);
    }

    static IEnumerable<Path64> OrderNear(Paths64 paths, (double X, double Y)? near)
    {
        if (paths.Count <= 1 || near is not { } p)
            return paths;
        return paths.OrderBy(path =>
        {
            var pts = ToPoints(path);
            if (pts.Count == 0) return double.PositiveInfinity;
            var i = NearestIndex(pts, p);
            var dx = pts[i].X - p.X;
            var dy = pts[i].Y - p.Y;
            return dx * dx + dy * dy;
        });
    }

    static PocketClearResult TooSmall(
        IReadOnlyList<(double X, double Y)> outline,
        double step,
        double inset)
    {
        var cx = outline.Average(p => p.X);
        var cy = outline.Average(p => p.Y);
        return new PocketClearResult
        {
            Path = [(cx, cy)],
            Segments = [],
            PassCount = 0,
            StepoverMm = step,
            InsetMm = inset,
            TooSmallForTool = true,
        };
    }

    static IReadOnlyList<(double X, double Y)> StartOnLongestStraight(
        IReadOnlyList<(double X, double Y)> loop)
    {
        if (loop.Count < 6)
            return loop;
        var pts = loop.ToList();
        var closed = Dist(pts[0], pts[^1]) < 1e-6;
        if (closed)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 5)
            return loop;

        var minX = pts.Min(p => p.X);
        var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxY = pts.Max(p => p.Y);
        var midX = (minX + maxX) / 2;
        var midY = (minY + maxY) / 2;
        const double band = 1.25;
        int best;
        if (maxX - minX >= maxY - minY)
        {
            var onEdge = pts
                .Select((p, i) => (p, i))
                .Where(t => Math.Abs(t.p.Y - minY) <= band)
                .ToList();
            best = onEdge.Count > 0
                ? onEdge.OrderBy(t => Math.Abs(t.p.X - midX)).First().i
                : 0;
        }
        else
        {
            var onEdge = pts
                .Select((p, i) => (p, i))
                .Where(t => Math.Abs(t.p.X - minX) <= band)
                .ToList();
            best = onEdge.Count > 0
                ? onEdge.OrderBy(t => Math.Abs(t.p.Y - midY)).First().i
                : 0;
        }

        RotateInPlace(pts, best);
        if (closed)
            pts.Add(pts[0]);
        return pts;
    }

    static IReadOnlyList<(double X, double Y)> StartNear(
        IReadOnlyList<(double X, double Y)> loop,
        (double X, double Y) near)
    {
        if (loop.Count < 3)
            return loop;
        var pts = loop.ToList();
        var closed = Dist(pts[0], pts[^1]) < 1e-6;
        if (closed)
            pts.RemoveAt(pts.Count - 1);
        if (pts.Count == 0)
            return loop;
        RotateInPlace(pts, NearestIndex(pts, near));
        if (closed)
            pts.Add(pts[0]);
        return pts;
    }

    static double Dist((double X, double Y) a, (double X, double Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static IReadOnlyList<(double X, double Y)> CloseRing(IReadOnlyList<(double X, double Y)> loop)
    {
        if (loop.Count < 3)
            return loop;
        var a = loop[0];
        var b = loop[^1];
        if (Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6)
            return loop;
        var closed = loop.ToList();
        closed.Add(a);
        return closed;
    }

    static double RingSpan(IReadOnlyList<(double X, double Y)> ring)
    {
        if (ring.Count == 0) return 0;
        var minX = ring.Min(p => p.X);
        var maxX = ring.Max(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxY = ring.Max(p => p.Y);
        return Math.Max(maxX - minX, maxY - minY);
    }

    static List<Path64> OffsetRings(Path64 outer, double stepMm)
    {
        var rings = new List<Path64> { outer };
        var current = new Paths64 { outer };
        for (var i = 0; i < 80; i++)
        {
            var next = Clipper.InflatePaths(
                current, -stepMm * Scale, JoinType.Round, EndType.Polygon);
            if (next.Count == 0) break;
            var ring = Largest(next);
            if (IsSliverRing(ring)) break;
            EnsureCcw(ring);
            rings.Add(ring);
            current = [ring];
        }
        return rings;
    }

    static List<(double X, double Y)> StitchSpiralInsideOut(
        IReadOnlyList<Path64> outerToInner,
        bool closeEachRing)
    {
        var spiral = new List<(double X, double Y)>();
        if (outerToInner.Count == 0) return spiral;

        (double X, double Y)? last = null;
        for (var r = outerToInner.Count - 1; r >= 0; r--)
        {
            var pts = ToPoints(outerToInner[r]);
            if (pts.Count < 3) continue;
            var start = last is { } p ? NearestIndex(pts, p) : MinXIndex(pts);
            RotateInPlace(pts, start);
            if (last is not null)
                spiral.Add(pts[0]);
            for (var i = 0; i < pts.Count; i++)
                spiral.Add(pts[i]);
            if (closeEachRing)
                spiral.Add(pts[0]);
            last = spiral[^1];
        }
        return spiral;
    }

    static Path64 Largest(Paths64 paths) =>
        paths.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();

    static void EnsureCcw(Path64 path)
    {
        if (Clipper.Area(path) < 0)
            path.Reverse();
    }

    static List<(double X, double Y)> ToPoints(Path64 path)
    {
        var pts = new List<(double X, double Y)>(path.Count);
        foreach (var p in path)
            pts.Add((p.X / Scale, p.Y / Scale));
        if (pts.Count >= 2)
        {
            var a = pts[0];
            var b = pts[^1];
            if (Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
        }
        return pts;
    }

    static IReadOnlyList<(double X, double Y)> ClosedLoop(
        Path64 region,
        (double X, double Y)? startNear = null)
    {
        var loop = ToPoints(region);
        if (startNear is { } p)
            RotateInPlace(loop, NearestIndex(loop, p));
        loop.Add(loop[0]);
        return loop;
    }

    static int NearestIndex(IReadOnlyList<(double X, double Y)> pts, (double X, double Y) p)
    {
        var best = 0;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < pts.Count; i++)
        {
            var dx = pts[i].X - p.X;
            var dy = pts[i].Y - p.Y;
            var d = dx * dx + dy * dy;
            if (d >= bestD) continue;
            bestD = d;
            best = i;
        }
        return best;
    }

    static int MinXIndex(IReadOnlyList<(double X, double Y)> pts)
    {
        var best = 0;
        for (var i = 1; i < pts.Count; i++)
            if (pts[i].X < pts[best].X) best = i;
        return best;
    }

    static void RotateInPlace(List<(double X, double Y)> pts, int start)
    {
        if (start <= 0 || start >= pts.Count) return;
        var head = pts.GetRange(0, start);
        pts.RemoveRange(0, start);
        pts.AddRange(head);
    }

    static Path64 ToPath64(IReadOnlyList<(double X, double Y)> pts)
    {
        var path = new Path64(pts.Count);
        foreach (var p in pts)
            path.Add(new Point64((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        return path;
    }
}
