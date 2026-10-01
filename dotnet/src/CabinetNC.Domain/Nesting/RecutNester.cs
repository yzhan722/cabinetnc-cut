namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>
/// Recut packer: pending replacement panels onto a <b>finite</b> stack of stock
/// (shop remnants first — rectangle or ortho L / U via <see cref="NestSheetSpec.Blocked"/> —
/// then whatever full sheets the operator agreed to open).
/// Unlike <see cref="BlfNester"/> the last sheet is never cloned — when the stock runs out the
/// panel is reported unplaced so the shell can ask before opening another full sheet.
/// Every open sheet of the matching material/thickness is tried before a new one is opened,
/// so a small part still goes back onto an earlier remnant with room left.
/// </summary>
public static class RecutNester
{
    public const string EngineName = "recut_finite_v1";
    public const string NoStockCode = "no_recut_stock";
    public const string NoFitCode = "recut_stock_exhausted";

    public static NestResult Pack(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestSheetSpec> stock,
        NestSettings settings,
        Func<Panel, (double w, double h)> sizeOf)
    {
        var placements = new List<NestPlacement>();
        var unplaced = new List<string>();
        var reasons = new List<NestUnplacedReason>();
        var reports = new List<NestGroupReport>();
        var sheetsUsed = new List<NestSheetSpec>();
        var used = new bool[stock.Count];

        var groups = panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();

        foreach (var group in groups)
        {
            var key = group.Key;
            var candidates = new List<int>();
            for (var i = 0; i < stock.Count; i++)
            {
                if (used[i]) continue;
                if (NestGroupKey.From(stock[i].Material, stock[i].ThicknessMm).Equals(key))
                    candidates.Add(i);
            }

            var items = group
                .Select(p =>
                {
                    var (w, h) = sizeOf(p);
                    return (Panel: p, W: w, H: h);
                })
                .Where(t => t.W > 0 && t.H > 0)
                .OrderByDescending(t => t.W * t.H)
                .ThenByDescending(t => Math.Max(t.W, t.H))
                .ToList();

            var open = new List<OpenSheet>();
            var groupStart = sheetsUsed.Count;
            var placed = 0;
            double usedArea = 0;

            foreach (var item in items)
            {
                if (candidates.Count == 0 && open.Count == 0)
                {
                    unplaced.Add(item.Panel.PanelId);
                    reasons.Add(new NestUnplacedReason
                    {
                        PanelId = item.Panel.PanelId,
                        Code = NoStockCode,
                        Message = $"没有 {key} 的余料或整板",
                    });
                    continue;
                }

                var mayRotate = settings.PanelMayRotate90(item.Panel);
                var done = false;

                // 1. every sheet already open for this group
                foreach (var sheet in open)
                {
                    if (TryPlace(sheet, item.Panel.PanelId, item.W, item.H, mayRotate, settings.ClearanceMm, placements))
                    {
                        done = true;
                        break;
                    }
                }

                // 2. open the next unused stock rectangle that can hold it at all
                while (!done && candidates.Count > 0)
                {
                    var idx = candidates[0];
                    candidates.RemoveAt(0);
                    used[idx] = true;
                    var spec = stock[idx];
                    var sheet = new OpenSheet(spec, sheetsUsed.Count);
                    sheetsUsed.Add(spec);
                    open.Add(sheet);
                    if (TryPlace(sheet, item.Panel.PanelId, item.W, item.H, mayRotate, settings.ClearanceMm, placements))
                        done = true;
                }

                if (done)
                {
                    placed++;
                    usedArea += item.W * item.H;
                    continue;
                }

                unplaced.Add(item.Panel.PanelId);
                reasons.Add(new NestUnplacedReason
                {
                    PanelId = item.Panel.PanelId,
                    Code = NoFitCode,
                    Message = $"{key} 的余料放不下 {item.W:0.#}×{item.H:0.#}",
                });
            }

            // Sheets that were opened but never received a part are dropped again.
            var kept = new List<NestSheetSpec>();
            var remap = new Dictionary<int, int>();
            for (var i = groupStart; i < sheetsUsed.Count; i++)
            {
                if (placements.Any(p => p.SheetIndex == i))
                {
                    remap[i] = groupStart + kept.Count;
                    kept.Add(sheetsUsed[i]);
                }
            }
            sheetsUsed.RemoveRange(groupStart, sheetsUsed.Count - groupStart);
            sheetsUsed.AddRange(kept);
            for (var i = 0; i < placements.Count; i++)
            {
                if (remap.TryGetValue(placements[i].SheetIndex, out var next) && next != placements[i].SheetIndex)
                {
                    var p = placements[i];
                    placements[i] = new NestPlacement
                    {
                        PanelId = p.PanelId,
                        SheetIndex = next,
                        OffsetX = p.OffsetX,
                        OffsetY = p.OffsetY,
                        RotationDeg = p.RotationDeg,
                    };
                }
            }

            double sheetArea = 0;
            foreach (var s in kept) sheetArea += s.WidthMm * s.LengthMm;
            reports.Add(new NestGroupReport
            {
                Key = key,
                PartCount = items.Count,
                PlacedCount = placed,
                SheetCount = kept.Count,
                LocalSheetStart = groupStart,
                UtilizationPct = sheetArea > 0 ? usedArea / sheetArea * 100 : 0,
            });
        }

        return new NestResult
        {
            Engine = EngineName,
            Placements = placements,
            SheetCount = sheetsUsed.Count,
            Unplaced = unplaced,
            UnplacedReasons = reasons,
            GroupReports = reports,
            SheetsUsed = sheetsUsed,
        };
    }

    sealed class OpenSheet
    {
        public OpenSheet(NestSheetSpec spec, int index)
        {
            Spec = spec;
            Index = index;
            Free = InitFree(spec);
        }

        public NestSheetSpec Spec { get; }
        public int Index { get; }
        public List<Rect> Free { get; set; }
    }

    /// <summary>
    /// Inner rectangle minus keep-outs (drawn L / U remnants). Same punch as
    /// <see cref="BlfNester"/> so a missing corner is never treated as stock.
    /// </summary>
    static List<Rect> InitFree(NestSheetSpec spec)
    {
        var (ix, iy, iw, ih) = spec.InnerRect();
        var free = new List<Rect> { new(ix, iy, iw, ih) };
        if (spec.Blocked.Count == 0) return free;
        var inset = spec.Insets();
        foreach (var b in spec.Blocked)
        {
            var x = Math.Max(b.MinX, inset.Left);
            var y = Math.Max(b.MinY, inset.Bottom);
            var x2 = Math.Min(b.MaxX, spec.WidthMm - inset.Right);
            var y2 = Math.Min(b.MaxY, spec.LengthMm - inset.Top);
            if (x2 <= x || y2 <= y) continue;
            free = Split(free, x, y, x2 - x, y2 - y);
        }

        return free;
    }

    static bool TryPlace(
        OpenSheet sheet,
        string panelId,
        double w,
        double h,
        bool mayRotate,
        double gap,
        List<NestPlacement> placements)
    {
        var orients = new List<(double w, double h, double rot)> { (w, h, 0) };
        if (mayRotate && Math.Abs(w - h) > 1e-6)
            orients.Add((h, w, 90));

        (Rect fr, double w, double h, double rot)? best = null;
        foreach (var o in orients)
        {
            if (!sheet.Spec.FitsLocalSize(o.w, o.h)) continue;
            foreach (var fr in sheet.Free.OrderBy(r => r.Y).ThenBy(r => r.X))
            {
                if (o.w <= fr.W + 1e-6 && o.h <= fr.H + 1e-6)
                {
                    if (best is null || fr.Y < best.Value.fr.Y
                        || (Math.Abs(fr.Y - best.Value.fr.Y) < 1e-9 && fr.X < best.Value.fr.X))
                        best = (fr, o.w, o.h, o.rot);
                    break;
                }
            }
        }

        if (best is null) return false;
        var b = best.Value;
        placements.Add(new NestPlacement
        {
            PanelId = panelId,
            SheetIndex = sheet.Index,
            OffsetX = b.fr.X,
            OffsetY = b.fr.Y,
            RotationDeg = b.rot,
        });
        sheet.Free = Split(sheet.Free, b.fr.X, b.fr.Y, b.w + Math.Max(0, gap), b.h + Math.Max(0, gap));
        return true;
    }

    static List<Rect> Split(List<Rect> free, double x, double y, double w, double h)
    {
        var next = new List<Rect>();
        foreach (var r in free)
        {
            if (x + w <= r.X || x >= r.X + r.W || y + h <= r.Y || y >= r.Y + r.H)
            {
                next.Add(r);
                continue;
            }
            if (x > r.X) next.Add(new Rect(r.X, r.Y, x - r.X, r.H));
            if (x + w < r.X + r.W) next.Add(new Rect(x + w, r.Y, r.X + r.W - (x + w), r.H));
            if (y > r.Y) next.Add(new Rect(r.X, r.Y, r.W, y - r.Y));
            if (y + h < r.Y + r.H) next.Add(new Rect(r.X, y + h, r.W, r.Y + r.H - (y + h)));
        }
        var sized = next.Where(a => a.W >= 1 && a.H >= 1).ToList();
        var result = new List<Rect>();
        for (var i = 0; i < sized.Count; i++)
        {
            var a = sized[i];
            var contained = false;
            for (var j = 0; j < sized.Count && !contained; j++)
            {
                if (i == j) continue;
                var b = sized[j];
                var inside = a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H;
                // identical rects: keep the first one only
                if (inside && (a != b || j < i)) contained = true;
            }
            if (!contained) result.Add(a);
        }
        return result;
    }

    readonly record struct Rect(double X, double Y, double W, double H);
}
