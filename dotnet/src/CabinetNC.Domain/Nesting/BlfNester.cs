namespace CabinetNC.Domain.Nesting;

public sealed class NestSheetSpec
{
    public double WidthMm { get; init; } = 1220;
    public double LengthMm { get; init; } = 2440;
    public double BorderMm { get; init; } = 15;
    /// <summary>Part-to-part clearance for this stock kind. Used when packing this material group.</summary>
    public double SpacingMm { get; init; } = 12;
    /// <summary>Allow 90° nest rotation for panels on this stock kind (still subject to grain lock).</summary>
    public bool AllowRotation { get; init; } = true;
    /// <summary>Request part-in-part nesting for this stock kind (engine may still ignore until implemented).</summary>
    public bool AllowPartsInPart { get; init; }
    /// <summary>Keep-out AABBs in sheet space.</summary>
    public IReadOnlyList<NestBlockedRect> Blocked { get; init; } = [];
    public string? Label { get; init; }
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
}

public sealed class NestBlockedRect
{
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }
}

public sealed class NestRequest
{
    public required IReadOnlyList<NestPart> Parts { get; init; }
    public double SheetWidthMm { get; init; } = 1220;
    public double SheetLengthMm { get; init; } = 2440;
    public double SpacingMm { get; init; } = 12;
    public double BorderMm { get; init; } = 15;
    public bool AllowRotation { get; init; } = true;
    /// <summary>If set, multi-sheet queue (primary + remnants). Else single sheet from Width/Length.</summary>
    public IReadOnlyList<NestSheetSpec>? Sheets { get; init; }
}

public sealed class NestPart
{
    public required string PanelId { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public bool MayRotate { get; init; } = true;
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
}

public sealed class NestPlacement
{
    public required string PanelId { get; init; }
    public int SheetIndex { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double RotationDeg { get; init; }
}

public sealed class NestResult
{
    public required string Engine { get; init; }
    public required IReadOnlyList<NestPlacement> Placements { get; init; }
    public int SheetCount { get; init; }
    public IReadOnlyList<string> Unplaced { get; init; } = [];
    public IReadOnlyList<NestUnplacedReason> UnplacedReasons { get; init; } = [];
    public IReadOnlyList<NestGroupReport> GroupReports { get; init; } = [];
    public IReadOnlyList<NestSheetSpec> SheetsUsed { get; init; } = [];
    /// <summary>Children packed into host through-cutout voids (parts-in-part).</summary>
    public IReadOnlyList<PartInPartSlot> PartInPartSlots { get; init; } = [];
}

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
                var innerW = spec.WidthMm - spec.BorderMm * 2;
                var innerH = spec.LengthMm - spec.BorderMm * 2;
                var orients = new List<(double w, double h, double rot)> { (item.WidthMm, item.HeightMm, 0) };
                if (req.AllowRotation && item.MayRotate && Math.Abs(item.WidthMm - item.HeightMm) > 1e-6)
                    orients.Add((item.HeightMm, item.WidthMm, 90));
                orients = orients.Where(o => o.w <= innerW && o.h <= innerH).ToList();
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
            Blocked = [], // ponytail: cloned sheets have no defects
            Label = s.Label,
        };

    static List<Rect> InitFree(NestSheetSpec spec)
    {
        var border = Math.Max(0, spec.BorderMm);
        var innerW = spec.WidthMm - border * 2;
        var innerH = spec.LengthMm - border * 2;
        var free = new List<Rect> { new(border, border, innerW, innerH) };
        foreach (var b in spec.Blocked)
        {
            var x = Math.Max(b.MinX, border);
            var y = Math.Max(b.MinY, border);
            var x2 = Math.Min(b.MaxX, spec.WidthMm - border);
            var y2 = Math.Min(b.MaxY, spec.LengthMm - border);
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
