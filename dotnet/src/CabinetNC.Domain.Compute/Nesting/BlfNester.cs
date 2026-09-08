namespace CabinetNC.Domain.Nesting;

/// <summary>
/// AABB free-rect BLF — port of src/pack.js blfPack.
/// Supports defect keep-outs + remnant sheet queue.
/// ponytail: O(n²) free-rect; upgrade = poly/NFP in Worker.
/// </summary>
public static class BlfNester
{
    public static NestResult Pack(NestRequest req)
    {
        var gap = Math.Max(0, req.SpacingMm);
        var sheetQueue = BuildSheetQueue(req);
        var sheetIndex = 0;
        var free = InitFree(sheetQueue[0]);

        var items = req.Parts
            .Where(p => p.WidthMm > 0 && p.HeightMm > 0)
            .OrderByDescending(p => p.WidthMm * p.HeightMm)
            .ThenByDescending(p => Math.Max(p.WidthMm, p.HeightMm))
            .ToList();

        var placements = new List<NestPlacement>();
        var unplaced = new List<string>();

        void NewSheet()
        {
            sheetIndex++;
            var spec = sheetIndex < sheetQueue.Count
                ? sheetQueue[sheetIndex]
                : CloneInfinite(sheetQueue[^1]);
            if (sheetIndex >= sheetQueue.Count)
                sheetQueue.Add(spec);
            free = InitFree(spec);
        }

        void SplitFree(double x, double y, double w, double h)
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
            free = next.Where(a => a.W >= 1 && a.H >= 1).Where((a, i) =>
            {
                for (var j = 0; j < next.Count; j++)
                {
                    if (i == j) continue;
                    var b = next[j];
                    if (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H)
                        return false;
                }
                return true;
            }).ToList();
        }

        foreach (var item in items)
        {
            var placed = false;
            for (var attempt = 0; attempt < sheetQueue.Count + items.Count + 2 && !placed; attempt++)
            {
                var spec = sheetQueue[Math.Min(sheetIndex, sheetQueue.Count - 1)];
                var orients = new List<(double w, double h, double rot)> { (item.WidthMm, item.HeightMm, 0) };
                if (req.AllowRotation && item.MayRotate && Math.Abs(item.WidthMm - item.HeightMm) > 1e-6)
                    orients.Add((item.HeightMm, item.WidthMm, 90));
                orients = orients.Where(o => spec.FitsLocalSize(o.w, o.h)).ToList();
                if (orients.Count == 0)
                {
                    unplaced.Add(item.PanelId);
                    placed = true;
                    break;
                }

                (Rect fr, double w, double h, double rot)? best = null;
                foreach (var o in orients)
                {
                    foreach (var fr in free.OrderBy(r => r.Y).ThenBy(r => r.X))
                    {
                        if (o.w <= fr.W && o.h <= fr.H)
                        {
                            if (best is null || fr.Y < best.Value.fr.Y || (fr.Y == best.Value.fr.Y && fr.X < best.Value.fr.X))
                                best = (fr, o.w, o.h, o.rot);
                            break;
                        }
                    }
                }

                if (best is null)
                {
                    NewSheet();
                    continue;
                }

                var b = best.Value;
                placements.Add(new NestPlacement
                {
                    PanelId = item.PanelId,
                    SheetIndex = sheetIndex,
                    OffsetX = b.fr.X,
                    OffsetY = b.fr.Y,
                    RotationDeg = b.rot,
                });
                SplitFree(b.fr.X, b.fr.Y, b.w + gap, b.h + gap);
                placed = true;
            }

            if (!placed)
                unplaced.Add(item.PanelId);
        }

        return new NestResult
        {
            Engine = "worker_blf_v0",
            Placements = placements,
            SheetCount = placements.Count == 0 ? 0 : sheetIndex + 1,
            Unplaced = unplaced,
        };
    }

    static List<NestSheetSpec> BuildSheetQueue(NestRequest req)
    {
        if (req.Sheets is { Count: > 0 })
            return req.Sheets.ToList();
        return
        [
            new NestSheetSpec
            {
                WidthMm = req.SheetWidthMm > 0 ? req.SheetWidthMm : 1220,
                LengthMm = req.SheetLengthMm > 0 ? req.SheetLengthMm : 2440,
                BorderMm = Math.Max(0, req.BorderMm),
            },
        ];
    }

    static NestSheetSpec CloneInfinite(NestSheetSpec s) =>
        new()
        {
            WidthMm = s.WidthMm,
            LengthMm = s.LengthMm,
            BorderMm = s.BorderMm,
            InsetLeftMm = s.InsetLeftMm,
            InsetBottomMm = s.InsetBottomMm,
            InsetRightMm = s.InsetRightMm,
            InsetTopMm = s.InsetTopMm,
            Blocked = [], // ponytail: cloned sheets have no defects
            Label = s.Label,
            Material = s.Material,
            ThicknessMm = s.ThicknessMm,
            SpacingMm = s.SpacingMm,
            AllowRotation = s.AllowRotation,
            AllowPartsInPart = s.AllowPartsInPart,
            SheetGrain = s.SheetGrain,
        };

    static List<Rect> InitFree(NestSheetSpec spec)
    {
        var (ix, iy, iw, ih) = spec.InnerRect();
        var free = new List<Rect> { new(ix, iy, iw, ih) };
        var inset = spec.Insets();
        foreach (var b in spec.Blocked)
        {
            var x = Math.Max(b.MinX, inset.Left);
            var y = Math.Max(b.MinY, inset.Bottom);
            var x2 = Math.Min(b.MaxX, spec.WidthMm - inset.Right);
            var y2 = Math.Min(b.MaxY, spec.LengthMm - inset.Top);
            if (x2 <= x || y2 <= y) continue;
            free = Punch(free, x, y, x2 - x, y2 - y);
        }
        return free;
    }

    static List<Rect> Punch(List<Rect> free, double x, double y, double w, double h)
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
        return next.Where(a => a.W >= 1 && a.H >= 1).ToList();
    }

    readonly record struct Rect(double X, double Y, double W, double H);
}
