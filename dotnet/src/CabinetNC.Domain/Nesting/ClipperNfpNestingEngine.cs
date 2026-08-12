namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;
using Clipper2Lib;

/// <summary>
/// Approximate NFP nest engine using Clipper2 MinkowskiDiff.
/// Outer contour only, 0/90° rotations, grouped by material+thickness.
/// Falls back to AABB contact candidates when NFP yields no legal slot.
/// </summary>
public sealed class ClipperNfpNestingEngine : INestingEngine
{
    const int MaxCandidates = 160;
    public string Name => "clipper_nfp_v0";

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
                    Done = done, Total = total, Message = $"NFP · 无板材 {key}",
                });
                continue;
            }

            var groupSettings = NestStockOverrides.ForGroup(settings, matched[0]);
            var ordered = usable
                .OrderByDescending(PolygonArea)
                .ThenBy(p => p.PanelId, StringComparer.Ordinal)
                .ToList();

            var sheets = new List<SheetState>();
            var placed = new List<NestPlacement>();
            var unplaced = new List<Panel>();

            foreach (var panel in ordered)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new NestProgressReport
                {
                    Done = done,
                    Total = total,
                    Message = $"NFP · {key} · {done + 1}/{total}",
                });

                Candidate? best = null;
                for (var si = 0; si < sheets.Count; si++)
                {
                    var c = BestOnSheet(panel, groupSettings, sheets[si], si, ct);
                    if (c is not null && (best is null || c.Score < best.Score))
                        best = c;
                }

                if (best is null)
                {
                    var spec = NextSheet(matched, sheets.Count, key);
                    var state = new SheetState(spec);
                    var c = BestOnSheet(panel, groupSettings, state, sheets.Count, ct);
                    if (c is null)
                    {
                        unplaced.Add(panel);
                        done++;
                        continue;
                    }
                    sheets.Add(state);
                    best = c;
                }

                var sheet = sheets[best.SheetIndex];
                sheet.Placed.Add(new PlacedPart(panel, best.Local, best.World, best.Bounds, best.Rotation));
                sheet.ConsumeFree(
                    best.X,
                    best.Y,
                    best.Local.Bounds.MaxX + Math.Max(0, groupSettings.ClearanceMm),
                    best.Local.Bounds.MaxY + Math.Max(0, groupSettings.ClearanceMm));
                placed.Add(new NestPlacement
                {
                    PanelId = panel.PanelId,
                    SheetIndex = best.SheetIndex,
                    OffsetX = best.X,
                    OffsetY = best.Y,
                    RotationDeg = best.Rotation,
                });
                done++;
            }

            foreach (var s in sheets)
                sheetsUsed.Add(s.Spec);
            foreach (var p in placed)
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
            foreach (var p in unplaced)
            {
                allUnplaced.Add(p.PanelId);
                reasons.Add(new NestUnplacedReason
                {
                    PanelId = p.PanelId,
                    Code = "does_not_fit",
                    Message = $"组 {key} 内 NFP 无法放入",
                });
            }

            var sheetArea = sheets.Sum(s => s.Spec.WidthMm * s.Spec.LengthMm);
            var used = placed.Sum(p => PolygonArea(usable.First(u => u.PanelId == p.PanelId)));
            reports.Add(new NestGroupReport
            {
                Key = key,
                PartCount = group.Count(),
                PlacedCount = placed.Count,
                SheetCount = sheets.Count,
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

    static Candidate? BestOnSheet(
        Panel panel,
        NestSettings settings,
        SheetState sheet,
        int sheetIndex,
        CancellationToken ct)
    {
        Candidate? best = null;
        var rotations = settings.PanelMayRotate90(panel) ? new[] { 0d, 90d } : new[] { 0d };
        foreach (var rot in rotations)
        {
            ct.ThrowIfCancellationRequested();
            var local = LocalNormalized(panel, rot);
            if (local.Points.Count < 3) continue;
            if (!FitsAabb(local.Bounds, sheet.Spec, sheet.Spec.BorderMm))
                continue;

            var movingPath = NfpGeometry.ToPath(local.Points);
            var clearance = Math.Max(0, settings.ClearanceMm);
            // NFP = hard-overlap map (no clearance inflate). Spacing enforced by Conflicts below.
            // Inflating here + Conflicts(clearance) double-counts and either wastes sheets or leaks gaps.
            var nfps = new Paths64();
            foreach (var placed in sheet.Placed)
            {
                foreach (var path in NfpGeometry.ComputeNfp(placed.WorldPath, movingPath))
                    nfps.Add(path);
            }
            foreach (var blocked in sheet.Spec.Blocked)
            {
                var rect = RectPath(blocked.MinX, blocked.MinY, blocked.MaxX, blocked.MaxY);
                foreach (var path in NfpGeometry.ComputeNfp(rect, movingPath))
                    nfps.Add(path);
            }

            var border = Math.Max(0, sheet.Spec.BorderMm);
            var w = local.Bounds.MaxX;
            var h = local.Bounds.MaxY;
            var candidates = new List<(double X, double Y)>();
            // Free-rect BL corners first (same density driver as grouped BLF).
            foreach (var fr in sheet.Free.OrderBy(r => r.Y).ThenBy(r => r.X))
            {
                if (w <= fr.W + 1e-6 && h <= fr.H + 1e-6)
                    candidates.Add((fr.X, fr.Y));
            }
            candidates.AddRange(NfpGeometry.CandidateReferences(nfps, border, MaxCandidates / 2));
            foreach (var placed in sheet.Placed)
            {
                candidates.Add((placed.Bounds.MaxX + clearance, placed.Bounds.MinY));
                candidates.Add((placed.Bounds.MinX, placed.Bounds.MaxY + clearance));
            }
            candidates = candidates
                .OrderBy(p => p.Y)
                .ThenBy(p => p.X)
                .Distinct()
                .Take(MaxCandidates)
                .ToList();

            var tested = 0;
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

    static bool FitsInSheet(Bounds local, double x, double y, NestSheetSpec sheet)
    {
        var border = Math.Max(0, sheet.BorderMm);
        return x >= border - 1e-6
               && y >= border - 1e-6
               && x + local.MaxX <= sheet.WidthMm - border + 1e-6
               && y + local.MaxY <= sheet.LengthMm - border + 1e-6;
    }

    static bool FitsAabb(Bounds local, NestSheetSpec sheet, double border)
    {
        var innerW = sheet.WidthMm - 2 * border;
        var innerH = sheet.LengthMm - 2 * border;
        return local.MaxX <= innerW + 1e-6 && local.MaxY <= innerH + 1e-6;
    }

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

    sealed class SheetState
    {
        public NestSheetSpec Spec { get; }
        public List<PlacedPart> Placed { get; } = [];
        public List<FreeRect> Free { get; private set; }

        public SheetState(NestSheetSpec spec)
        {
            Spec = spec;
            var border = Math.Max(0, spec.BorderMm);
            Free =
            [
                new FreeRect(
                    border,
                    border,
                    Math.Max(0, spec.WidthMm - 2 * border),
                    Math.Max(0, spec.LengthMm - 2 * border)),
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
