namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;
using Clipper2Lib;

/// <summary>
/// NFP nest engine: convex-decomp Minkowski NFP, edge slide, a few order trials.
/// Outer contour only, 0/90/180/270° (see <see cref="NestSettings.CandidateRotations"/>), grouped by material+thickness.
/// Falls back to AABB contact candidates when NFP yields no legal slot.
/// </summary>
public sealed class ClipperNfpNestingEngine : INestingEngine
{
    const int MaxCandidates = 220;
    const int MaxValidFits = 10;
    public string Name => "clipper_nfp_v1";

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
        var cache = new NfpCache();

        var groups = panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();

        var total = Math.Max(1, panels.Count);
        var done = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var key = group.Key;
            var matched = GroupedBlfNester.MatchSheets(stockTemplates, key);
            var start = sheetsUsed.Count;
            var usable = group.Where(p => p.Outline.Points.Count >= 3).ToList();
            foreach (var bad in group.Where(p => p.Outline.Points.Count < 3))
            {
                allUnplaced.Add(bad.PanelId);
                reasons.Add(new NestUnplacedReason
                {
                    PanelId = bad.PanelId,
                    Code = "invalid_outline",
                    Message = "板件轮廓不足 3 个点",
                });
                done++;
            }

            if (matched.Count == 0)
            {
                foreach (var p in usable)
                {
                    allUnplaced.Add(p.PanelId);
                    reasons.Add(new NestUnplacedReason
                    {
                        PanelId = p.PanelId,
                        Code = "no_stock_for_group",
                        Message = $"无匹配板材：{key}",
                    });
                    done++;
                }
                reports.Add(new NestGroupReport
                {
                    Key = key,
                    PartCount = group.Count(),
                    PlacedCount = 0,
                    SheetCount = 0,
                    LocalSheetStart = start,
                });
                progress?.Report(new NestProgressReport
                {
                    Done = done, Total = total, Message = $"精排 · 无板材 {key}",
                });
                continue;
            }

            var groupSettings = NestStockOverrides.ForGroup(settings, matched[0]);
            var orders = BuildOrders(usable);
            GroupTrial? bestTrial = null;
            for (var oi = 0; oi < orders.Count; oi++)
            {
                ct.ThrowIfCancellationRequested();
                var trial = PackOrder(
                    orders[oi],
                    groupSettings,
                    matched,
                    key,
                    cache,
                    ct,
                    progress,
                    total,
                    doneBase: done,
                    orderLabel: orders.Count == 1
                        ? $"精排 · {key}"
                        : $"精排 · {key} · 试排 {oi + 1}/{orders.Count}");
                if (bestTrial is null || trial.IsBetterThan(bestTrial))
                    bestTrial = trial;
                if (trial.Unplaced.Count == 0 && trial.Sheets.Count <= 1)
                    break;
            }

            var packed = bestTrial ?? new GroupTrial();
            done += usable.Count;
            foreach (var s in packed.Sheets)
                sheetsUsed.Add(s.Spec);
            foreach (var p in packed.Placements)
            {
                allPlacements.Add(new NestPlacement
                {
                    PanelId = p.PanelId,
                    SheetIndex = start + p.SheetIndex,
                    OffsetX = p.OffsetX,
                    OffsetY = p.OffsetY,
                    RotationDeg = p.RotationDeg,
                });
            }
            foreach (var p in packed.Unplaced)
            {
                allUnplaced.Add(p.PanelId);
                reasons.Add(new NestUnplacedReason
                {
                    PanelId = p.PanelId,
                    Code = "does_not_fit",
                    Message = $"组 {key} 内 NFP 无法放入",
                });
            }

            var sheetArea = packed.Sheets.Sum(s => s.Spec.WidthMm * s.Spec.LengthMm);
            var used = packed.Placements.Sum(p => PolygonArea(usable.First(u => u.PanelId == p.PanelId)));
            reports.Add(new NestGroupReport
            {
                Key = key,
                PartCount = group.Count(),
                PlacedCount = packed.Placements.Count,
                SheetCount = packed.Sheets.Count,
                LocalSheetStart = start,
                UtilizationPct = sheetArea > 0 ? used / sheetArea * 100 : 0,
            });
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

    static IReadOnlyList<IReadOnlyList<Panel>> BuildOrders(IReadOnlyList<Panel> usable)
    {
        if (usable.Count == 0) return [];
        var area = usable
            .OrderByDescending(PolygonArea)
            .ThenBy(p => p.PanelId, StringComparer.Ordinal)
            .ToList();
        var orders = new List<IReadOnlyList<Panel>> { area };
        if (usable.Count <= 36)
        {
            orders.Add(usable
                .OrderByDescending(p =>
                {
                    var b = NestTransform.BoundsOf(p);
                    return Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
                })
                .ThenBy(p => p.PanelId, StringComparer.Ordinal)
                .ToList());
        }
        if (usable.Count <= 20)
        {
            var swaps = Math.Min(3, area.Count - 1);
            for (var i = 0; i < swaps; i++)
            {
                var mutated = area.ToList();
                (mutated[i], mutated[i + 1]) = (mutated[i + 1], mutated[i]);
                orders.Add(mutated);
            }
        }
        return orders;
    }

    static GroupTrial PackOrder(
        IReadOnlyList<Panel> ordered,
        NestSettings groupSettings,
        IReadOnlyList<NestSheetSpec> matched,
        NestGroupKey key,
        NfpCache cache,
        CancellationToken ct,
        IProgress<NestProgressReport>? progress,
        int total,
        int doneBase,
        string orderLabel)
    {
        var trial = new GroupTrial();
        var placedCount = 0;
        foreach (var panel in ordered)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new NestProgressReport
            {
                Done = doneBase + placedCount,
                Total = total,
                Message = $"{orderLabel} · {placedCount + 1}/{ordered.Count}",
            });

            Candidate? best = null;
            for (var si = 0; si < trial.Sheets.Count; si++)
            {
                var c = BestOnSheet(panel, groupSettings, trial.Sheets[si], si, cache, ct);
                if (c is not null && (best is null || c.Score < best.Score))
                    best = c;
            }

            if (best is null)
            {
                var spec = NextSheet(matched, trial.Sheets.Count, key);
                var state = new SheetState(spec);
                var c = BestOnSheet(panel, groupSettings, state, trial.Sheets.Count, cache, ct);
                if (c is null)
                {
                    trial.Unplaced.Add(panel);
                    placedCount++;
                    continue;
                }
                trial.Sheets.Add(state);
                best = c;
            }

            var sheet = trial.Sheets[best.SheetIndex];
            sheet.Placed.Add(new PlacedPart(panel, best.Local, best.World, best.Bounds, best.Rotation));
            sheet.ConsumeFree(
                best.X,
                best.Y,
                best.Local.Bounds.MaxX + Math.Max(0, groupSettings.ClearanceMm),
                best.Local.Bounds.MaxY + Math.Max(0, groupSettings.ClearanceMm));
            trial.Placements.Add(new NestPlacement
            {
                PanelId = panel.PanelId,
                SheetIndex = best.SheetIndex,
                OffsetX = best.X,
                OffsetY = best.Y,
                RotationDeg = best.Rotation,
            });
            placedCount++;
        }
        return trial;
    }

    static Candidate? BestOnSheet(
        Panel panel,
        NestSettings settings,
        SheetState sheet,
        int sheetIndex,
        NfpCache cache,
        CancellationToken ct)
    {
        Candidate? best = null;
        var rotations = settings.CandidateRotations(panel);
        foreach (var rot in rotations)
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalNormalized(panel, rot);
            if (local.Points.Count < 3) continue;
            if (!sheet.Spec.FitsLocalSize(local.Bounds.MaxX, local.Bounds.MaxY))
                continue;

            var movingPath = NfpGeometry.ToPath(local.Points);
            var clearance = Math.Max(0, settings.ClearanceMm);
            var nfps = new Paths64();
            foreach (var placed in sheet.Placed)
            {
                foreach (var path in cache.Get(placed.WorldPath, movingPath))
                    nfps.Add(path);
            }
            foreach (var blocked in sheet.Spec.Blocked)
            {
                var rect = RectPath(blocked.MinX, blocked.MinY, blocked.MaxX, blocked.MaxY);
                foreach (var path in cache.Get(rect, movingPath))
                    nfps.Add(path);
            }

            var inset = sheet.Spec.Insets();
            var w = local.Bounds.MaxX;
            var h = local.Bounds.MaxY;
            var candidates = new List<(double X, double Y)>();
            foreach (var fr in sheet.Free.OrderBy(r => r.Y).ThenBy(r => r.X))
            {
                if (w <= fr.W + 1e-6 && h <= fr.H + 1e-6)
                    candidates.Add((fr.X, fr.Y));
            }
            candidates.AddRange(NfpGeometry.CandidateReferences(nfps, inset.Left, inset.Bottom, MaxCandidates / 2));
            foreach (var placed in sheet.Placed)
            {
                candidates.Add((placed.Bounds.MaxX + clearance, placed.Bounds.MinY));
                candidates.Add((placed.Bounds.MinX, placed.Bounds.MaxY + clearance));
            }
            foreach (var blocked in sheet.Spec.Blocked)
            {
                candidates.Add((blocked.MaxX + clearance, blocked.MinY));
                candidates.Add((blocked.MinX, blocked.MaxY + clearance));
            }
            candidates = candidates
                .OrderBy(p => p.Y)
                .ThenBy(p => p.X)
                .Distinct()
                .Take(MaxCandidates)
                .ToList();

            var tested = 0;
            var validFits = 0;
            foreach (var (x, y) in candidates)
            {
                if (++tested > MaxCandidates) break;
                if ((tested & 31) == 0) ct.ThrowIfCancellationRequested();
                if (!FitsInSheet(local.Bounds, x, y, sheet.Spec)) continue;
                if (NfpGeometry.ReferenceForbidden(x, y, nfps)) continue;

                var world = ShiftPath(movingPath, x, y);
                if (Conflicts(world, local.Bounds, x, y, sheet, clearance)) continue;

                var score = (y + local.Bounds.MaxY) * Math.Max(1, sheet.Spec.WidthMm) + (x + local.Bounds.MaxX);
                var c = new Candidate(sheetIndex, x, y, rot, local, world, BoundsAt(local.Bounds, x, y), score);
                if (best is null || c.Score < best.Score)
                    best = c;
                if (++validFits >= MaxValidFits)
                    break;
            }
        }
        return best;
    }

    static bool Conflicts(
        Path64 world,
        Bounds localBounds,
        double x,
        double y,
        SheetState sheet,
        double clearance)
    {
        var bounds = BoundsAt(localBounds, x, y);
        foreach (var placed in sheet.Placed)
        {
            if (!AabbsNear(bounds, placed.Bounds, clearance)) continue;
            if (PathsConflict(world, placed.WorldPath, clearance))
                return true;
        }
        foreach (var blocked in sheet.Spec.Blocked)
        {
            var rect = RectPath(blocked.MinX, blocked.MinY, blocked.MaxX, blocked.MaxY);
            if (PathsConflict(world, rect, clearance))
                return true;
        }
        return false;
    }

    static bool PathsConflict(Path64 a, Path64 b, double clearance)
    {
        Paths64 aa = [a];
        Paths64 bb = [b];
        var half = Math.Max(0, clearance) * NfpGeometry.Scale / 2;
        if (half > 0)
        {
            aa = Clipper.InflatePaths(aa, half, JoinType.Round, EndType.Polygon);
            bb = Clipper.InflatePaths(bb, half, JoinType.Round, EndType.Polygon);
        }
        return Clipper.Intersect(aa, bb, FillRule.NonZero)
            .Any(p => Math.Abs(Clipper.Area(p)) > 1);
    }

    static bool AabbsNear(Bounds a, Bounds b, double gap) =>
        !(a.MaxX + gap <= b.MinX || b.MaxX + gap <= a.MinX
          || a.MaxY + gap <= b.MinY || b.MaxY + gap <= a.MinY);

    static bool FitsInSheet(Bounds local, double x, double y, NestSheetSpec sheet) =>
        sheet.ContainsBox(x + local.MinX, y + local.MinY, x + local.MaxX, y + local.MaxY, 1e-6);

    static LocalShape LocalNormalized(Panel panel, double rotationDeg)
    {
        var r = rotationDeg * Math.PI / 180;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        var rotated = panel.Outline.Points
            .Select(p => (X: p.X * c - p.Y * s, Y: p.X * s + p.Y * c))
            .ToList();
        var minX = rotated.Min(p => p.X);
        var minY = rotated.Min(p => p.Y);
        var points = rotated.Select(p => (p.X - minX, p.Y - minY)).ToList();
        var maxX = points.Max(p => p.Item1);
        var maxY = points.Max(p => p.Item2);
        return new LocalShape(points, new Bounds(0, 0, maxX, maxY));
    }

    static Path64 ShiftPath(Path64 local, double x, double y)
    {
        var dx = (long)Math.Round(x * NfpGeometry.Scale);
        var dy = (long)Math.Round(y * NfpGeometry.Scale);
        return new Path64(local.Select(p => new Point64(p.X + dx, p.Y + dy)));
    }

    static Path64 RectPath(double minX, double minY, double maxX, double maxY) =>
        NfpGeometry.ToPath([(minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)]);

    static Bounds BoundsAt(Bounds local, double x, double y) =>
        new(x + local.MinX, y + local.MinY, x + local.MaxX, y + local.MaxY);

    static NestSheetSpec NextSheet(IReadOnlyList<NestSheetSpec> templates, int index, NestGroupKey key)
    {
        var src = index < templates.Count ? templates[index] : templates[^1];
        return new NestSheetSpec
        {
            WidthMm = src.WidthMm,
            LengthMm = src.LengthMm,
            BorderMm = src.BorderMm,
            InsetLeftMm = src.InsetLeftMm,
            InsetBottomMm = src.InsetBottomMm,
            InsetRightMm = src.InsetRightMm,
            InsetTopMm = src.InsetTopMm,
            SpacingMm = src.SpacingMm,
            AllowRotation = src.AllowRotation,
            AllowPartsInPart = src.AllowPartsInPart,
            Blocked = index < templates.Count ? src.Blocked : [],
            Label = string.IsNullOrWhiteSpace(src.Label)
                ? $"{key.Material}_{key.ThicknessMm:0.##}"
                : src.Label,
            Material = key.Material,
            ThicknessMm = key.ThicknessMm,
            SheetGrain = src.SheetGrain,
        };
    }

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

    readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY);
    sealed record LocalShape(List<(double X, double Y)> Points, Bounds Bounds);
    sealed record PlacedPart(Panel Panel, LocalShape Local, Path64 WorldPath, Bounds Bounds, double Rotation);
    sealed record Candidate(
        int SheetIndex, double X, double Y, double Rotation,
        LocalShape Local, Path64 World, Bounds Bounds, double Score);
    readonly record struct FreeRect(double X, double Y, double W, double H);

    sealed class GroupTrial
    {
        public List<NestPlacement> Placements { get; } = [];
        public List<Panel> Unplaced { get; } = [];
        public List<SheetState> Sheets { get; } = [];

        public bool IsBetterThan(GroupTrial other)
        {
            if (Unplaced.Count != other.Unplaced.Count)
                return Unplaced.Count < other.Unplaced.Count;
            if (Sheets.Count != other.Sheets.Count)
                return Sheets.Count < other.Sheets.Count;
            return Extent() < other.Extent();
        }

        double Extent() =>
            Sheets.Sum(s => s.Placed.Count == 0 ? 0 : s.Placed.Max(p => p.Bounds.MaxY));
    }

    sealed class NfpCache
    {
        readonly Dictionary<(long, long), Paths64> _map = [];

        public Paths64 Get(Path64 fixedPath, Path64 moving)
        {
            var key = (Hash(fixedPath), Hash(moving));
            if (_map.TryGetValue(key, out var hit))
                return hit;
            var nfp = NfpGeometry.ComputeNfp(fixedPath, moving);
            _map[key] = nfp;
            return nfp;
        }

        static long Hash(Path64 path)
        {
            unchecked
            {
                long h = path.Count * 397L;
                foreach (var p in path)
                    h = h * 31 + p.X * 17 + p.Y;
                return h;
            }
        }
    }

    sealed class SheetState
    {
        public NestSheetSpec Spec { get; }
        public List<PlacedPart> Placed { get; } = [];
        public List<FreeRect> Free { get; private set; }

        public SheetState(NestSheetSpec spec)
        {
            Spec = spec;
            var (ix, iy, iw, ih) = spec.InnerRect();
            Free =
            [
                new FreeRect(ix, iy, iw, ih),
            ];
            foreach (var b in spec.Blocked)
                ConsumeFree(b.MinX, b.MinY, Math.Max(0, b.MaxX - b.MinX), Math.Max(0, b.MaxY - b.MinY));
        }

        public void ConsumeFree(double x, double y, double w, double h)
        {
            var next = new List<FreeRect>();
            foreach (var r in Free)
            {
                if (x + w <= r.X || x >= r.X + r.W || y + h <= r.Y || y >= r.Y + r.H)
                {
                    next.Add(r);
                    continue;
                }
                if (x > r.X) next.Add(new FreeRect(r.X, r.Y, x - r.X, r.H));
                if (x + w < r.X + r.W) next.Add(new FreeRect(x + w, r.Y, r.X + r.W - (x + w), r.H));
                if (y > r.Y) next.Add(new FreeRect(r.X, r.Y, r.W, y - r.Y));
                if (y + h < r.Y + r.H) next.Add(new FreeRect(r.X, y + h, r.W, r.Y + r.H - (y + h)));
            }
            Free = next.Where(a => a.W >= 1 && a.H >= 1).ToList();
        }
    }
}
