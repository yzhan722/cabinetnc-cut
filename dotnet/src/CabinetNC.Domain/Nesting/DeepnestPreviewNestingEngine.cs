namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;
using Clipper2Lib;

/// <summary>
/// Deepnest-style preview engine for the CabinetNC UI.
///
/// This is deliberately isolated behind <see cref="INestingEngine"/>. It uses actual
/// panel polygons, edge/vertex candidate placement and deterministic order mutations.
/// It is not a vendored copy of Deepnest Next; a future Node/Rust/commercial adapter can
/// replace this class without changing Desktop or the nesting result contract.
/// </summary>
public sealed class DeepnestPreviewNestingEngine : INestingEngine
{
    const double Scale = 1000;
    /// <summary>Hard cap on placement candidate positions tested per panel×rotation×sheet.</summary>
    const int MaxCandidates = 96;
    /// <summary>Outline sample points used to seed contact candidates (was ~24).</summary>
    const int MaxSamplePoints = 8;
    /// <summary>Stop after this many valid fits — keep the best among them (bottom-left biased).</summary>
    const int MaxValidFits = 3;

    public string Name => "deepnest_preview_v0";

    public NestResult Pack(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null)
    {
        _ = sizeOf;
        var allPlacements = new List<NestPlacement>();
        var allUnplaced = new List<string>();
        var reasons = new List<NestUnplacedReason>();
        var reports = new List<NestGroupReport>();
        var sheetsUsed = new List<NestSheetSpec>();

        var groups = panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();

        // Work units ≈ sum(orderCount × panelCount) across groups.
        var totalUnits = 0;
        var orderCounts = new int[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            var usable = groups[i].Where(HasUsableOutline).ToList();
            var orders = usable.Count == 0 ? 1 : BuildOrders(usable).Count;
            orderCounts[i] = orders;
            totalUnits += Math.Max(1, orders * Math.Max(1, usable.Count));
        }
        totalUnits = Math.Max(1, totalUnits);
        var doneUnits = 0;

        for (var gi = 0; gi < groups.Count; gi++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[gi];
            var key = group.Key;
            var groupPanels = group.Where(HasUsableOutline).ToList();
            var invalid = group.Where(p => !HasUsableOutline(p)).ToList();
            var matched = GroupedBlfNester.MatchSheets(stockTemplates, key);
            var globalSheetStart = sheetsUsed.Count;

            if (matched.Count == 0)
            {
                foreach (var panel in group)
                    AddUnplaced(panel, "no_stock_for_group", $"无匹配板材：{key}", allUnplaced, reasons);
                reports.Add(Report(key, group.Count(), 0, 0, globalSheetStart, 0));
                doneUnits += orderCounts[gi] * Math.Max(1, groupPanels.Count);
                progress?.Report(new NestProgressReport
                {
                    Done = doneUnits,
                    Total = totalUnits,
                    Message = $"无匹配板材 · {key}",
                });
                continue;
            }

            foreach (var panel in invalid)
                AddUnplaced(panel, "invalid_outline", "板件轮廓不足 3 个点", allUnplaced, reasons);

            var groupSettings = NestStockOverrides.ForGroup(settings, matched[0]);
            var best = FindBest(
                groupPanels,
                groupSettings,
                matched,
                key,
                ct,
                progress,
                totalUnits,
                ref doneUnits,
                orderCounts[gi]);
            foreach (var sheet in best.Sheets)
                sheetsUsed.Add(sheet.Spec);
            foreach (var place in best.Placements)
            {
                allPlacements.Add(new NestPlacement
                {
                    PanelId = place.PanelId,
                    SheetIndex = globalSheetStart + place.SheetIndex,
                    OffsetX = place.OffsetX,
                    OffsetY = place.OffsetY,
                    RotationDeg = place.RotationDeg,
                });
            }
            foreach (var panel in best.Unplaced)
                AddUnplaced(panel, "does_not_fit", $"组 {key} 内无法放入（轮廓/边距/缺陷）", allUnplaced, reasons);

            var sheetArea = best.Sheets.Sum(s => s.Spec.WidthMm * s.Spec.LengthMm);
            var usedArea = best.Placements.Sum(p =>
                PolygonArea(groupPanels.First(x => x.PanelId == p.PanelId)));
            reports.Add(Report(
                key,
                group.Count(),
                best.Placements.Count,
                best.Sheets.Count,
                globalSheetStart,
                sheetArea > 0 ? usedArea / sheetArea * 100 : 0));
        }

        return new NestResult
        {
            Engine = Name,
            Placements = allPlacements,
            SheetCount = sheetsUsed.Count,
            Unplaced = allUnplaced,
            UnplacedReasons = reasons,
            GroupReports = reports,
            SheetsUsed = sheetsUsed,
        };
    }

    static Trial FindBest(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> templates,
        NestGroupKey key,
        CancellationToken ct,
        IProgress<NestProgressReport>? progress,
        int totalUnits,
        ref int doneUnits,
        int expectedOrders)
    {
        if (panels.Count == 0)
        {
            doneUnits += Math.Max(1, expectedOrders);
            return new Trial();
        }

        var orders = BuildOrders(panels);
        Trial? best = null;
        for (var oi = 0; oi < orders.Count; oi++)
        {
            ct.ThrowIfCancellationRequested();
            var order = orders[oi];
            var trial = PackOrder(
                order,
                settings,
                templates,
                key,
                ct,
                progress,
                totalUnits,
                ref doneUnits,
                orderLabel: $"{key} · 试排 {oi + 1}/{orders.Count}");
            if (best is null || trial.Score.CompareTo(best.Score) < 0)
                best = trial;
        }
        _ = expectedOrders;
        return best!;
    }

    static IReadOnlyList<IReadOnlyList<Panel>> BuildOrders(IReadOnlyList<Panel> panels)
    {
        var orders = new List<IReadOnlyList<Panel>>
        {
            panels.OrderByDescending(PolygonArea).ThenBy(p => p.PanelId, StringComparer.Ordinal).ToList(),
            panels.OrderByDescending(p => {
                var b = NestTransform.BoundsOf(p);
                return Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
            }).ThenBy(p => p.PanelId, StringComparer.Ordinal).ToList(),
        };
        if (panels.Count <= 60)
        {
            orders.Add(panels.OrderByDescending(p => {
                var b = NestTransform.BoundsOf(p);
                return b.MaxX - b.MinX;
            }).ThenByDescending(PolygonArea).ToList());
            orders.Add(panels.OrderByDescending(p => {
                var b = NestTransform.BoundsOf(p);
                return b.MaxY - b.MinY;
            }).ThenByDescending(PolygonArea).ToList());
        }

        // Deterministic adjacent swaps provide a small GA-like population without
        // making shop output depend on process-global random state.
        var seed = orders[0].ToList();
        var mutationCount = panels.Count <= 20 ? 8 : panels.Count <= 60 ? 3 : 0;
        for (var i = 0; i + 1 < Math.Min(seed.Count, mutationCount); i++)
        {
            var mutated = seed.ToList();
            (mutated[i], mutated[i + 1]) = (mutated[i + 1], mutated[i]);
            orders.Add(mutated);
        }
        return orders;
    }

    static Trial PackOrder(
        IReadOnlyList<Panel> order,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> templates,
        NestGroupKey key,
        CancellationToken ct,
        IProgress<NestProgressReport>? progress,
        int totalUnits,
        ref int doneUnits,
        string orderLabel)
    {
        var trial = new Trial();
        for (var pi = 0; pi < order.Count; pi++)
        {
            ct.ThrowIfCancellationRequested();
            var panel = order[pi];
            progress?.Report(new NestProgressReport
            {
                Done = doneUnits,
                Total = totalUnits,
                Message = $"{orderLabel} · {pi + 1}/{order.Count}",
            });

            Candidate? best = null;
            for (var sheetIndex = 0; sheetIndex < trial.Sheets.Count; sheetIndex++)
            {
                var local = NestStockOverrides.ForGroup(settings, trial.Sheets[sheetIndex].Spec);
                var candidate = BestOnSheet(panel, local, trial.Sheets[sheetIndex], sheetIndex, ct);
                if (candidate is not null && (best is null || candidate.Score < best.Score))
                    best = candidate;
            }

            if (best is null)
            {
                var spec = NextSheet(templates, trial.Sheets.Count, key);
                var state = new SheetState(spec);
                var local = NestStockOverrides.ForGroup(settings, spec);
                var candidate = BestOnSheet(panel, local, state, trial.Sheets.Count, ct);
                if (candidate is null)
                {
                    trial.Unplaced.Add(panel);
                    doneUnits++;
                    continue;
                }
                trial.Sheets.Add(state);
                best = candidate;
            }

            var sheet = trial.Sheets[best.SheetIndex];
            sheet.Placed.Add(new PlacedShape(panel, best.Path, best.Bounds));
            trial.Placements.Add(new NestPlacement
            {
                PanelId = panel.PanelId,
                SheetIndex = best.SheetIndex,
                OffsetX = best.X,
                OffsetY = best.Y,
                RotationDeg = best.Rotation,
            });
            doneUnits++;
        }
        return trial;
    }

    static Candidate? BestOnSheet(
        Panel panel,
        NestSettings settings,
        SheetState sheet,
        int sheetIndex,
        CancellationToken ct)
    {
        Candidate? best = null;
        var validFits = 0;
        var tested = 0;
        var rotations = settings.PanelMayRotate90(panel) ? new[] { 0d, 90d } : new[] { 0d };
        foreach (var rotation in rotations)
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalShape(panel, rotation);
            foreach (var (x, y) in CandidateOffsets(local, sheet, settings.ClearanceMm))
            {
                if (++tested > MaxCandidates)
                    return best;
                if ((tested & 15) == 0)
                    ct.ThrowIfCancellationRequested();

                var path = Shift(local.Path, x, y);
                var bounds = Shift(local.Bounds, x, y);
                if (!FitsSheet(path, bounds, sheet, settings.ClearanceMm)) continue;
                var score = bounds.MaxY * Math.Max(1, sheet.Spec.WidthMm) + bounds.MaxX;
                var candidate = new Candidate(sheetIndex, x, y, rotation, path, bounds, score);
                if (best is null || candidate.Score < best.Score)
                    best = candidate;
                if (++validFits >= MaxValidFits)
                    return best;
            }
        }
        return best;
    }

    static IReadOnlyList<(double X, double Y)> CandidateOffsets(
        LocalPolygon moving,
        SheetState sheet,
        double clearance)
    {
        var border = Math.Max(0, sheet.Spec.BorderMm);
        var points = new Dictionary<(long X, long Y), (double X, double Y)>();
        void Add(double x, double y)
        {
            if (points.Count >= MaxCandidates * 4) return;
            if (x < border - 1e-7 || y < border - 1e-7) return;
            var key = ((long)Math.Round(x * 100), (long)Math.Round(y * 100));
            points.TryAdd(key, (x, y));
        }

        Add(border, border);
        var xs = new HashSet<double> { border };
        var ys = new HashSet<double> { border };
        // AABB edges are cheap — keep all. Vertex-pair seeds are expensive — only last few shapes.
        foreach (var fixedShape in sheet.Placed)
        {
            xs.Add(fixedShape.Bounds.MaxX + clearance);
            xs.Add(fixedShape.Bounds.MinX);
            ys.Add(fixedShape.Bounds.MaxY + clearance);
            ys.Add(fixedShape.Bounds.MinY);
            Add(fixedShape.Bounds.MaxX + clearance, fixedShape.Bounds.MinY);
            Add(fixedShape.Bounds.MinX, fixedShape.Bounds.MaxY + clearance);
        }

        var vertexStart = Math.Max(0, sheet.Placed.Count - 4);
        var movingSamples = Sample(moving.Path).ToList();
        for (var i = vertexStart; i < sheet.Placed.Count; i++)
        {
            var fixedShape = sheet.Placed[i];
            foreach (var fp in Sample(fixedShape.Path))
            foreach (var mp in movingSamples)
            {
                var dx = (fp.X - mp.X) / Scale;
                var dy = (fp.Y - mp.Y) / Scale;
                Add(dx + clearance, dy);
                Add(dx - clearance, dy);
                Add(dx, dy + clearance);
                Add(dx, dy - clearance);
            }
        }

        // Sparse AABB grid — lowest Y/X first so Take() keeps bottom-left candidates.
        var xList = xs.OrderBy(v => v).Take(24).ToList();
        var yList = ys.OrderBy(v => v).Take(24).ToList();
        foreach (var y in yList)
        foreach (var x in xList)
            Add(x, y);

        return points.Values
            .OrderBy(p => p.Y)
            .ThenBy(p => p.X)
            .Take(MaxCandidates)
            .ToList();
    }

    static bool FitsSheet(Path64 path, Bounds bounds, SheetState sheet, double clearance)
    {
        var border = Math.Max(0, sheet.Spec.BorderMm);
        if (bounds.MinX < border - 1e-6 || bounds.MinY < border - 1e-6
            || bounds.MaxX > sheet.Spec.WidthMm - border + 1e-6
            || bounds.MaxY > sheet.Spec.LengthMm - border + 1e-6)
            return false;

        foreach (var blocked in sheet.Spec.Blocked)
        {
            var blockedPath = RectPath(
                blocked.MinX, blocked.MinY, blocked.MaxX, blocked.MaxY);
            if (PathsConflict(path, blockedPath, clearance))
                return false;
        }

        foreach (var placed in sheet.Placed)
        {
            if (!AabbsNear(bounds, placed.Bounds, clearance)) continue;
            if (PathsConflict(path, placed.Path, clearance))
                return false;
        }
        return true;
    }

    static bool PathsConflict(Path64 a, Path64 b, double clearance)
    {
        Paths64 aa = [a];
        Paths64 bb = [b];
        var halfGap = Math.Max(0, clearance) * Scale / 2;
        if (halfGap > 0)
        {
            aa = Clipper.InflatePaths(aa, halfGap, JoinType.Round, EndType.Polygon);
            bb = Clipper.InflatePaths(bb, halfGap, JoinType.Round, EndType.Polygon);
        }
        return Clipper.Intersect(aa, bb, FillRule.NonZero)
            .Any(p => Math.Abs(Clipper.Area(p)) > 1);
    }

    static bool AabbsNear(Bounds a, Bounds b, double gap) =>
        !(a.MaxX + gap <= b.MinX || b.MaxX + gap <= a.MinX
          || a.MaxY + gap <= b.MinY || b.MaxY + gap <= a.MinY);

    static LocalPolygon LocalShape(Panel panel, double rotation)
    {
        var radians = rotation * Math.PI / 180;
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        var rotated = panel.Outline.Points
            .Select(p => (X: p.X * c - p.Y * s, Y: p.X * s + p.Y * c))
            .ToList();
        var minX = rotated.Min(p => p.X);
        var minY = rotated.Min(p => p.Y);
        var normalized = rotated.Select(p => (p.X - minX, p.Y - minY)).ToList();
        var bounds = BoundsOf(normalized);
        return new LocalPolygon(ToPath(normalized), bounds);
    }

    static Path64 Shift(Path64 source, double x, double y)
    {
        var dx = (long)Math.Round(x * Scale);
        var dy = (long)Math.Round(y * Scale);
        return new Path64(source.Select(p => new Point64(p.X + dx, p.Y + dy)));
    }

    static Bounds Shift(Bounds b, double x, double y) =>
        new(b.MinX + x, b.MinY + y, b.MaxX + x, b.MaxY + y);

    static Path64 ToPath(IEnumerable<(double X, double Y)> points) =>
        new(points.Select(p => new Point64(
            (long)Math.Round(p.X * Scale),
            (long)Math.Round(p.Y * Scale))));

    static Path64 RectPath(double minX, double minY, double maxX, double maxY) =>
        ToPath([(minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)]);

    static Bounds BoundsOf(IReadOnlyList<(double X, double Y)> points) =>
        new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

    static IEnumerable<Point64> Sample(Path64 path)
    {
        if (path.Count == 0) yield break;
        var count = Math.Min(MaxSamplePoints, path.Count);
        if (count == 1)
        {
            yield return path[0];
            yield break;
        }
        for (var i = 0; i < count; i++)
        {
            var idx = i * (path.Count - 1) / (count - 1);
            yield return path[idx];
        }
    }

    static NestSheetSpec NextSheet(
        IReadOnlyList<NestSheetSpec> templates,
        int index,
        NestGroupKey key)
    {
        var src = index < templates.Count ? templates[index] : templates[^1];
        return new NestSheetSpec
        {
            WidthMm = src.WidthMm,
            LengthMm = src.LengthMm,
            BorderMm = src.BorderMm,
            SpacingMm = src.SpacingMm,
            AllowRotation = src.AllowRotation,
            AllowPartsInPart = src.AllowPartsInPart,
            Blocked = index < templates.Count ? src.Blocked : [],
            Label = string.IsNullOrWhiteSpace(src.Label)
                ? $"{key.Material}_{key.ThicknessMm:0.##}"
                : src.Label,
            Material = key.Material,
            ThicknessMm = key.ThicknessMm,
        };
    }

    static bool HasUsableOutline(Panel panel) =>
        panel.Outline.Points.Count >= 3;

    static double PolygonArea(Panel panel)
    {
        var pts = panel.Outline.Points;
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2;
    }

    static void AddUnplaced(
        Panel panel,
        string code,
        string message,
        ICollection<string> ids,
        ICollection<NestUnplacedReason> reasons)
    {
        ids.Add(panel.PanelId);
        reasons.Add(new NestUnplacedReason
        {
            PanelId = panel.PanelId,
            Code = code,
            Message = message,
        });
    }

    static NestGroupReport Report(
        NestGroupKey key, int parts, int placed, int sheets, int start, double utilization) =>
        new()
        {
            Key = key,
            PartCount = parts,
            PlacedCount = placed,
            SheetCount = sheets,
            LocalSheetStart = start,
            UtilizationPct = utilization,
        };

    readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY);
    sealed record LocalPolygon(Path64 Path, Bounds Bounds);
    sealed record PlacedShape(Panel Panel, Path64 Path, Bounds Bounds);
    sealed record Candidate(
        int SheetIndex, double X, double Y, double Rotation,
        Path64 Path, Bounds Bounds, double Score);

    sealed class SheetState(NestSheetSpec spec)
    {
        public NestSheetSpec Spec { get; } = spec;
        public List<PlacedShape> Placed { get; } = [];
    }

    sealed class Trial
    {
        public List<NestPlacement> Placements { get; } = [];
        public List<Panel> Unplaced { get; } = [];
        public List<SheetState> Sheets { get; } = [];
        public (int Unplaced, int Sheets, double Extent) Score =>
            (Unplaced.Count, Sheets.Count, Sheets.Sum(s =>
                s.Placed.Count == 0 ? 0 : s.Placed.Max(p => p.Bounds.MaxY)));
    }
}
