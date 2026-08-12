namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>
/// Preview guillotine / remnant split for a nested sheet:
/// straight H, straight V, or one 90° L — outside the used nest AABB + clearance.
/// Remnant strip thickness must be ≥ <see cref="MinRemnantEdgeMm"/> or the candidate is skipped.
/// </summary>
public static class GuillotineCutPlanner
{
    public const double DefaultClearanceMm = 20;
    public const double MinRemnantEdgeMm = 400;

    public sealed class Result
    {
        public required string Kind { get; init; } // vertical | horizontal | L
        public required IReadOnlyList<(double X, double Y)> Polyline { get; init; }
        public double RemnantAreaMm2 { get; init; }
        public double RemnantMinEdgeMm { get; init; }
        public string? Label { get; init; }
    }

    public static Result? PlanForSheet(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double clearanceMm = DefaultClearanceMm,
        double minRemnantEdgeMm = MinRemnantEdgeMm)
    {
        if (sheetW <= 0 || sheetH <= 0) return null;
        var byId = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var onSheet = placements.Where(p => p.SheetIndex == sheetIndex).ToList();
        if (onSheet.Count == 0) return null;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var any = false;
        foreach (var place in onSheet)
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            minX = Math.Min(minX, box.MinX);
            minY = Math.Min(minY, box.MinY);
            maxX = Math.Max(maxX, box.MaxX);
            maxY = Math.Max(maxY, box.MaxY);
            any = true;
        }
        if (!any) return null;

        var gap = Math.Max(0, clearanceMm);
        // Used region = nest contour AABB + clearance (cut stays outside parts).
        var uMinX = Math.Max(0, minX - gap);
        var uMinY = Math.Max(0, minY - gap);
        var uMaxX = Math.Min(sheetW, maxX + gap);
        var uMaxY = Math.Min(sheetH, maxY + gap);
        if (uMaxX <= uMinX || uMaxY <= uMinY) return null;

        var candidates = new List<Result>();
        var minE = Math.Max(0, minRemnantEdgeMm);

        // Straight vertical: keep used on one side, salvage the other strip.
        TryVertical(candidates, uMinX, sheetW, sheetH, minE, cutAtUsedMin: true);
        TryVertical(candidates, uMaxX, sheetW, sheetH, minE, cutAtUsedMin: false);

        // Straight horizontal
        TryHorizontal(candidates, uMinY, sheetW, sheetH, minE, cutAtUsedMin: true);
        TryHorizontal(candidates, uMaxY, sheetW, sheetH, minE, cutAtUsedMin: false);

        // L: only when BOTH leftover strips are ≥ min edge (avoids skinny corner guillotines).
        TryL(candidates, uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, minE);

        if (candidates.Count == 0) return null;
        // L only enters when both remnant arms ≥ min edge (see TryL) — that alone
        // prevents skinny corner cuts. Among legal candidates pick largest salvage.
        return candidates
            .OrderByDescending(c => c.RemnantAreaMm2)
            .ThenBy(c => c.Kind == "L" ? 1 : 0)
            .First();
    }

    static void TryVertical(
        List<Result> into,
        double x,
        double sheetW,
        double sheetH,
        double minE,
        bool cutAtUsedMin)
    {
        if (x <= 1e-6 || x >= sheetW - 1e-6) return;
        double remnantW;
        string label;
        if (cutAtUsedMin)
        {
            remnantW = x; // left strip [0, x]
            label = $"竖切 x={x:0.#} · 左余料 {remnantW:0.#}×{sheetH:0.#}";
        }
        else
        {
            remnantW = sheetW - x; // right strip
            label = $"竖切 x={x:0.#} · 右余料 {remnantW:0.#}×{sheetH:0.#}";
        }
        if (remnantW < minE - 1e-6) return;
        into.Add(new Result
        {
            Kind = "vertical",
            Polyline = [(x, 0), (x, sheetH)],
            RemnantAreaMm2 = remnantW * sheetH,
            RemnantMinEdgeMm = Math.Min(remnantW, sheetH),
            Label = label,
        });
    }

    static void TryHorizontal(
        List<Result> into,
        double y,
        double sheetW,
        double sheetH,
        double minE,
        bool cutAtUsedMin)
    {
        if (y <= 1e-6 || y >= sheetH - 1e-6) return;
        double remnantH;
        string label;
        if (cutAtUsedMin)
        {
            remnantH = y;
            label = $"横切 y={y:0.#} · 下余料 {sheetW:0.#}×{remnantH:0.#}";
        }
        else
        {
            remnantH = sheetH - y;
            label = $"横切 y={y:0.#} · 上余料 {sheetW:0.#}×{remnantH:0.#}";
        }
        if (remnantH < minE - 1e-6) return;
        into.Add(new Result
        {
            Kind = "horizontal",
            Polyline = [(0, y), (sheetW, y)],
            RemnantAreaMm2 = sheetW * remnantH,
            RemnantMinEdgeMm = Math.Min(sheetW, remnantH),
            Label = label,
        });
    }

    static void TryL(
        List<Result> into,
        double uMinX,
        double uMinY,
        double uMaxX,
        double uMaxY,
        double sheetW,
        double sheetH,
        double minE)
    {
        // Four L orientations: cut hugs the used AABB corner and runs out to two sheet edges.
        // Remnant must have both arm thicknesses ≥ minE.
        void Add(
            string corner,
            double armW,
            double armH,
            IReadOnlyList<(double X, double Y)> poly,
            double area)
        {
            if (armW < minE - 1e-6 || armH < minE - 1e-6) return;
            if (poly.Count < 3) return;
            into.Add(new Result
            {
                Kind = "L",
                Polyline = poly,
                RemnantAreaMm2 = area,
                RemnantMinEdgeMm = Math.Min(armW, armH),
                Label = $"L切 · {corner} · 臂 {armW:0.#}/{armH:0.#}",
            });
        }

        // Used bottom-left → remnant is top+right L (outside used max corner)
        {
            var armW = sheetW - uMaxX;
            var armH = sheetH - uMaxY;
            var area = armW * sheetH + uMaxX * armH;
            Add("右上", armW, armH,
                [(uMaxX, sheetH), (uMaxX, uMaxY), (sheetW, uMaxY)],
                area);
        }
        // Used bottom-right → remnant top+left
        {
            var armW = uMinX;
            var armH = sheetH - uMaxY;
            var area = armW * sheetH + (sheetW - uMinX) * armH;
            Add("左上", armW, armH,
                [(uMinX, sheetH), (uMinX, uMaxY), (0, uMaxY)],
                area);
        }
        // Used top-left → remnant bottom+right
        {
            var armW = sheetW - uMaxX;
            var armH = uMinY;
            var area = armW * sheetH + uMaxX * armH;
            Add("右下", armW, armH,
                [(uMaxX, 0), (uMaxX, uMinY), (sheetW, uMinY)],
                area);
        }
        // Used top-right → remnant bottom+left
        {
            var armW = uMinX;
            var armH = uMinY;
            var area = armW * sheetH + (sheetW - uMinX) * armH;
            Add("左下", armW, armH,
                [(uMinX, 0), (uMinX, uMinY), (0, uMinY)],
                area);
        }
    }
}
