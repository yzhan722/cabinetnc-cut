namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

/// <summary>
/// Remnant split outside the nest AABB + clearance.
/// <see cref="PlanForSheet"/> picks the largest single H / V / L cut.
/// <see cref="PlanSheet"/> returns every salvageable piece: rectangles first,
/// an L when splitting would make a piece shorter than <see cref="MinRemnantEdgeMm"/>.
/// </summary>
public static class GuillotineCutPlanner
{
    public const double DefaultClearanceMm = 20;
    public const double MinRemnantEdgeMm = 400;
    public const string OpKind = "remnant";
    public const string FeatureId = "guillotine";

    public sealed class Result
    {
        public required string Kind { get; init; } // vertical | horizontal | L
        public required IReadOnlyList<(double X, double Y)> Polyline { get; init; }
        public double RemnantAreaMm2 { get; init; }
        public double RemnantMinEdgeMm { get; init; }
        public string? Label { get; init; }
    }

    public sealed class RemnantPiece
    {
        public required string Shape { get; init; } // RECT | L
        public required double W { get; init; }
        public required double H { get; init; }
        public double AreaMm2 { get; init; }
        public double MinEdgeMm { get; init; }
        public double LabelX { get; init; }
        public double LabelY { get; init; }
        public string? Label { get; init; }
    }

    public sealed class SheetPlan
    {
        public required IReadOnlyList<Result> Cuts { get; init; }
        public IReadOnlyList<RemnantPiece> Pieces { get; init; } = [];
        public string? Label { get; init; }
        public double RemnantAreaMm2 { get; init; }
        public double RemnantMinEdgeMm { get; init; }

        public string Kind => Cuts.Count switch
        {
            0 => "",
            1 => Cuts[0].Kind,
            _ => "MULTI",
        };

        public IReadOnlyList<(double X, double Y)> Polyline =>
            Cuts.Count > 0 ? Cuts[0].Polyline : [];
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
        if (!TryUsedAabb(panels, placements, sheetIndex, sheetW, sheetH, clearanceMm,
                out var uMinX, out var uMinY, out var uMaxX, out var uMaxY))
            return null;

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

    /// <summary>
    /// All salvageable leftover pieces on a sheet. Rectangles first; an L is kept
    /// when splitting an adjacent pair would make a piece shorter than
    /// <paramref name="minRemnantEdgeMm"/>.
    /// </summary>
    public static SheetPlan? PlanSheet(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double clearanceMm = DefaultClearanceMm,
        double minRemnantEdgeMm = MinRemnantEdgeMm)
    {
        if (!TryUsedAabb(panels, placements, sheetIndex, sheetW, sheetH, clearanceMm,
                out var uMinX, out var uMinY, out var uMaxX, out var uMaxY))
            return null;

        var minE = Math.Max(0, minRemnantEdgeMm);
        var bot = uMinY;
        var top = sheetH - uMaxY;
        var left = uMinX;
        var right = sheetW - uMaxX;
        var midW = uMaxX - uMinX;
        var midH = uMaxY - uMinY;
        if (bot < 0.5 && top < 0.5 && left < 0.5 && right < 0.5) return null;

        SheetPlan? best = null;
        void Consider(Layout lay)
        {
            if (lay.Pieces.Count == 0) return;
            var area = lay.Pieces.Sum(p => p.AreaMm2);
            var minEdge = lay.Pieces.Min(p => p.MinEdgeMm);
            var label = string.Join(" + ", lay.Pieces.Select(p => p.Label).Where(s => !string.IsNullOrWhiteSpace(s)));
            var cand = new SheetPlan
            {
                Cuts = lay.Cuts,
                Pieces = lay.Pieces,
                Label = label,
                RemnantAreaMm2 = area,
                RemnantMinEdgeMm = minEdge,
            };
            if (Better(cand, best)) best = cand;
        }

        Consider(LayoutFourStrip(uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE));
        Consider(LayoutVFull(uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midW, minE));
        foreach (var corner in new[] { "TR", "TL", "BR", "BL" })
        {
            Consider(LayoutAdjacent(corner, twoRects: true, splitA: true,
                uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE));
            Consider(LayoutAdjacent(corner, twoRects: true, splitA: false,
                uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE));
            Consider(LayoutAdjacent(corner, twoRects: false, splitA: true,
                uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE));
        }

        return best;
    }

    sealed class Layout
    {
        public List<Result> Cuts { get; } = [];
        public List<RemnantPiece> Pieces { get; } = [];
    }

    static bool Better(SheetPlan cand, SheetPlan? best)
    {
        if (best is null) return true;
        // Salvage first so an L is kept when the only alternative drops an arm.
        // Among equal area, prefer more rectangles (split) over one L.
        if (Math.Abs(cand.RemnantAreaMm2 - best.RemnantAreaMm2) > 1)
            return cand.RemnantAreaMm2 > best.RemnantAreaMm2;
        var candRects = cand.Pieces.Count(p => p.Shape == "RECT");
        var bestRects = best.Pieces.Count(p => p.Shape == "RECT");
        if (candRects != bestRects) return candRects > bestRects;
        var candL = cand.Pieces.Count(p => p.Shape == "L");
        var bestL = best.Pieces.Count(p => p.Shape == "L");
        if (candL != bestL) return candL < bestL;
        return cand.Cuts.Count < best.Cuts.Count;
    }

    static Layout LayoutFourStrip(
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        var lay = new Layout();
        TryAddRect(lay, sheetW, bot, sheetW * 0.5, bot * 0.5, minE,
            HCut(0, uMinY, sheetW, sheetW, bot));
        TryAddRect(lay, sheetW, top, sheetW * 0.5, uMaxY + top * 0.5, minE,
            HCut(0, uMaxY, sheetW, sheetW, top));
        TryAddRect(lay, left, midH, left * 0.5, uMinY + midH * 0.5, minE,
            VCut(uMinX, uMinY, uMaxY, left, midH));
        TryAddRect(lay, right, midH, uMaxX + right * 0.5, uMinY + midH * 0.5, minE,
            VCut(uMaxX, uMinY, uMaxY, right, midH));
        return lay;
    }

    static Layout LayoutVFull(
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midW, double minE)
    {
        var lay = new Layout();
        TryAddRect(lay, left, sheetH, left * 0.5, sheetH * 0.5, minE,
            VCut(uMinX, 0, sheetH, left, sheetH));
        TryAddRect(lay, right, sheetH, uMaxX + right * 0.5, sheetH * 0.5, minE,
            VCut(uMaxX, 0, sheetH, right, sheetH));
        TryAddRect(lay, midW, bot, uMinX + midW * 0.5, bot * 0.5, minE,
            HCut(uMinX, uMinY, uMaxX, midW, bot));
        TryAddRect(lay, midW, top, uMinX + midW * 0.5, uMaxY + top * 0.5, minE,
            HCut(uMinX, uMaxY, uMaxX, midW, top));
        return lay;
    }

    static Layout LayoutAdjacent(
        string corner, bool twoRects, bool splitA,
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        var lay = new Layout();
        var ok = corner switch
        {
            "TR" => AddTr(lay, twoRects, splitA, uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE),
            "TL" => AddTl(lay, twoRects, splitA, uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE),
            "BR" => AddBr(lay, twoRects, splitA, uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE),
            _ => AddBl(lay, twoRects, splitA, uMinX, uMinY, uMaxX, uMaxY, sheetW, sheetH, bot, top, left, right, midH, minE),
        };
        return ok ? lay : new Layout();
    }

    static bool AddTr(Layout lay, bool twoRects, bool splitA,
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        if (right < 0.5 || top < 0.5) return false;
        if (twoRects)
        {
            if (splitA)
            {
                if (!CanRect(right, sheetH, minE) || !CanRect(uMaxX, top, minE)) return false;
                TryAddRect(lay, right, sheetH, uMaxX + right * 0.5, sheetH * 0.5, minE,
                    VCut(uMaxX, 0, sheetH, right, sheetH));
                TryAddRect(lay, uMaxX, top, uMaxX * 0.5, uMaxY + top * 0.5, minE,
                    HCut(0, uMaxY, uMaxX, uMaxX, top));
            }
            else
            {
                if (!CanRect(sheetW, top, minE) || !CanRect(right, uMaxY, minE)) return false;
                TryAddRect(lay, sheetW, top, sheetW * 0.5, uMaxY + top * 0.5, minE,
                    HCut(0, uMaxY, sheetW, sheetW, top));
                TryAddRect(lay, right, uMaxY, uMaxX + right * 0.5, uMaxY * 0.5, minE,
                    VCut(uMaxX, 0, uMaxY, right, uMaxY));
            }
        }
        else
        {
            if (right < minE - 1e-6 || top < minE - 1e-6) return false;
            AddL(lay, right, top, uMaxX + right * 0.35, uMaxY + top * 0.35,
                [(uMaxX, sheetH), (uMaxX, uMaxY), (sheetW, uMaxY)],
                right * sheetH + uMaxX * top);
        }
        TryAddRect(lay, uMaxX, bot, uMaxX * 0.5, bot * 0.5, minE, HCut(0, uMinY, uMaxX, uMaxX, bot));
        TryAddRect(lay, left, midH, left * 0.5, uMinY + midH * 0.5, minE, VCut(uMinX, uMinY, uMaxY, left, midH));
        return lay.Pieces.Count > 0;
    }

    static bool AddTl(Layout lay, bool twoRects, bool splitA,
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        if (left < 0.5 || top < 0.5) return false;
        var midRight = sheetW - uMinX;
        if (twoRects)
        {
            if (splitA)
            {
                if (!CanRect(left, sheetH, minE) || !CanRect(midRight, top, minE)) return false;
                TryAddRect(lay, left, sheetH, left * 0.5, sheetH * 0.5, minE,
                    VCut(uMinX, 0, sheetH, left, sheetH));
                TryAddRect(lay, midRight, top, uMinX + midRight * 0.5, uMaxY + top * 0.5, minE,
                    HCut(uMinX, uMaxY, sheetW, midRight, top));
            }
            else
            {
                if (!CanRect(sheetW, top, minE) || !CanRect(left, uMaxY, minE)) return false;
                TryAddRect(lay, sheetW, top, sheetW * 0.5, uMaxY + top * 0.5, minE,
                    HCut(0, uMaxY, sheetW, sheetW, top));
                TryAddRect(lay, left, uMaxY, left * 0.5, uMaxY * 0.5, minE,
                    VCut(uMinX, 0, uMaxY, left, uMaxY));
            }
        }
        else
        {
            if (left < minE - 1e-6 || top < minE - 1e-6) return false;
            AddL(lay, left, top, uMinX * 0.5, uMaxY + top * 0.35,
                [(uMinX, sheetH), (uMinX, uMaxY), (0, uMaxY)],
                left * sheetH + (sheetW - uMinX) * top);
        }
        TryAddRect(lay, sheetW - uMinX, bot, uMinX + (sheetW - uMinX) * 0.5, bot * 0.5, minE,
            HCut(uMinX, uMinY, sheetW, sheetW - uMinX, bot));
        TryAddRect(lay, right, midH, uMaxX + right * 0.5, uMinY + midH * 0.5, minE,
            VCut(uMaxX, uMinY, uMaxY, right, midH));
        return lay.Pieces.Count > 0;
    }

    static bool AddBr(Layout lay, bool twoRects, bool splitA,
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        if (right < 0.5 || bot < 0.5) return false;
        if (twoRects)
        {
            if (splitA)
            {
                if (!CanRect(right, sheetH, minE) || !CanRect(uMaxX, bot, minE)) return false;
                TryAddRect(lay, right, sheetH, uMaxX + right * 0.5, sheetH * 0.5, minE,
                    VCut(uMaxX, 0, sheetH, right, sheetH));
                TryAddRect(lay, uMaxX, bot, uMaxX * 0.5, bot * 0.5, minE,
                    HCut(0, uMinY, uMaxX, uMaxX, bot));
            }
            else
            {
                if (!CanRect(sheetW, bot, minE) || !CanRect(right, sheetH - uMinY, minE)) return false;
                TryAddRect(lay, sheetW, bot, sheetW * 0.5, bot * 0.5, minE,
                    HCut(0, uMinY, sheetW, sheetW, bot));
                TryAddRect(lay, right, sheetH - uMinY, uMaxX + right * 0.5, uMinY + (sheetH - uMinY) * 0.5, minE,
                    VCut(uMaxX, uMinY, sheetH, right, sheetH - uMinY));
            }
        }
        else
        {
            if (right < minE - 1e-6 || bot < minE - 1e-6) return false;
            AddL(lay, right, bot, uMaxX + right * 0.35, bot * 0.5,
                [(uMaxX, 0), (uMaxX, uMinY), (sheetW, uMinY)],
                right * sheetH + uMaxX * bot);
        }
        TryAddRect(lay, uMaxX, top, uMaxX * 0.5, uMaxY + top * 0.5, minE, HCut(0, uMaxY, uMaxX, uMaxX, top));
        TryAddRect(lay, left, midH, left * 0.5, uMinY + midH * 0.5, minE, VCut(uMinX, uMinY, uMaxY, left, midH));
        return lay.Pieces.Count > 0;
    }

    static bool AddBl(Layout lay, bool twoRects, bool splitA,
        double uMinX, double uMinY, double uMaxX, double uMaxY,
        double sheetW, double sheetH,
        double bot, double top, double left, double right, double midH, double minE)
    {
        if (left < 0.5 || bot < 0.5) return false;
        var midRight = sheetW - uMinX;
        if (twoRects)
        {
            if (splitA)
            {
                if (!CanRect(left, sheetH, minE) || !CanRect(midRight, bot, minE)) return false;
                TryAddRect(lay, left, sheetH, left * 0.5, sheetH * 0.5, minE,
                    VCut(uMinX, 0, sheetH, left, sheetH));
                TryAddRect(lay, midRight, bot, uMinX + midRight * 0.5, bot * 0.5, minE,
                    HCut(uMinX, uMinY, sheetW, midRight, bot));
            }
            else
            {
                if (!CanRect(sheetW, bot, minE) || !CanRect(left, sheetH - uMinY, minE)) return false;
                TryAddRect(lay, sheetW, bot, sheetW * 0.5, bot * 0.5, minE,
                    HCut(0, uMinY, sheetW, sheetW, bot));
                TryAddRect(lay, left, sheetH - uMinY, left * 0.5, uMinY + (sheetH - uMinY) * 0.5, minE,
                    VCut(uMinX, uMinY, sheetH, left, sheetH - uMinY));
            }
        }
        else
        {
            if (left < minE - 1e-6 || bot < minE - 1e-6) return false;
            AddL(lay, left, bot, uMinX * 0.5, bot * 0.5,
                [(uMinX, 0), (uMinX, uMinY), (0, uMinY)],
                left * sheetH + (sheetW - uMinX) * bot);
        }
        TryAddRect(lay, midRight, top, uMinX + midRight * 0.5, uMaxY + top * 0.5, minE,
            HCut(uMinX, uMaxY, sheetW, midRight, top));
        TryAddRect(lay, right, midH, uMaxX + right * 0.5, uMinY + midH * 0.5, minE,
            VCut(uMaxX, uMinY, uMaxY, right, midH));
        return lay.Pieces.Count > 0;
    }

    static bool CanRect(double w, double h, double minE) =>
        w >= minE - 1e-6 && h >= minE - 1e-6 && w > 0.5 && h > 0.5;

    static void TryAddRect(Layout lay, double w, double h, double lx, double ly, double minE, Result? cut)
    {
        if (!CanRect(w, h, minE)) return;
        lay.Pieces.Add(new RemnantPiece
        {
            Shape = "RECT",
            W = w,
            H = h,
            AreaMm2 = w * h,
            MinEdgeMm = Math.Min(w, h),
            LabelX = lx,
            LabelY = ly,
            Label = $"方 {w:0.#}×{h:0.#}",
        });
        if (cut is not null) lay.Cuts.Add(cut);
    }

    static void AddL(Layout lay, double armW, double armH, double lx, double ly,
        IReadOnlyList<(double X, double Y)> poly, double area)
    {
        lay.Pieces.Add(new RemnantPiece
        {
            Shape = "L",
            W = Math.Max(armW, armH),
            H = Math.Min(armW, armH),
            AreaMm2 = area,
            MinEdgeMm = Math.Min(armW, armH),
            LabelX = lx,
            LabelY = ly,
            Label = $"L {armW:0.#}×{armH:0.#}",
        });
        lay.Cuts.Add(new Result
        {
            Kind = "L",
            Polyline = poly,
            RemnantAreaMm2 = area,
            RemnantMinEdgeMm = Math.Min(armW, armH),
            Label = $"L切 · 臂 {armW:0.#}/{armH:0.#}",
        });
    }

    static Result? VCut(double x, double y0, double y1, double remnantW, double remnantH)
    {
        if (Math.Abs(y1 - y0) < 0.5) return null;
        return new Result
        {
            Kind = "vertical",
            Polyline = [(x, y0), (x, y1)],
            RemnantAreaMm2 = remnantW * remnantH,
            RemnantMinEdgeMm = Math.Min(remnantW, remnantH),
            Label = $"竖切 x={x:0.#} · 余料 {remnantW:0.#}×{remnantH:0.#}",
        };
    }

    static Result? HCut(double x0, double y, double x1, double remnantW, double remnantH)
    {
        if (Math.Abs(x1 - x0) < 0.5) return null;
        return new Result
        {
            Kind = "horizontal",
            Polyline = [(x0, y), (x1, y)],
            RemnantAreaMm2 = remnantW * remnantH,
            RemnantMinEdgeMm = Math.Min(remnantW, remnantH),
            Label = $"横切 y={y:0.#} · 余料 {remnantW:0.#}×{remnantH:0.#}",
        };
    }

    static bool TryUsedAabb(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double clearanceMm,
        out double uMinX, out double uMinY, out double uMaxX, out double uMaxY)
    {
        uMinX = uMinY = uMaxX = uMaxY = 0;
        if (sheetW <= 0 || sheetH <= 0) return false;
        var byId = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var onSheet = placements.Where(p => p.SheetIndex == sheetIndex).ToList();
        if (onSheet.Count == 0) return false;

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
        if (!any) return false;

        var gap = Math.Max(0, clearanceMm);
        uMinX = Math.Max(0, minX - gap);
        uMinY = Math.Max(0, minY - gap);
        uMaxX = Math.Min(sheetW, maxX + gap);
        uMaxY = Math.Min(sheetH, maxY + gap);
        return uMaxX > uMinX && uMaxY > uMinY;
    }

    /// <summary>
    /// Sheet-space through-cut on the preview polyline. Endpoints overshoot the
    /// sheet by half the tool so the kerf severs the edge.
    /// </summary>
    public static CutOp? ToCutOp(
        Result plan,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double thicknessMm,
        double toolDiameterMm = 10)
    {
        if (plan.Polyline.Count < 2) return null;
        _ = sheetW;
        _ = sheetH;
        var overshoot = Math.Max(0, toolDiameterMm) * 0.5;
        var path = Overshoot(plan.Polyline, overshoot);
        var th = thicknessMm > 0 ? thicknessMm : 18;
        return new CutOp
        {
            Op = OpKind,
            PanelId = $"SHEET-{sheetIndex}-REMNANT",
            FeatureId = FeatureId,
            Placed = true,
            Enabled = true,
            SheetIndex = sheetIndex,
            Path = path,
            ClosePath = false,
            Through = true,
            ToolId = "T2",
            ThicknessMm = th,
            DepthMm = CamSafety.OuterContourDepthMm(th),
        };
    }

    public static IReadOnlyList<CutOp> ToCutOps(
        SheetPlan plan,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double thicknessMm,
        double toolDiameterMm = 10)
    {
        var ops = new List<CutOp>(plan.Cuts.Count);
        for (var i = 0; i < plan.Cuts.Count; i++)
        {
            var op = ToCutOp(plan.Cuts[i], sheetIndex, sheetW, sheetH, thicknessMm, toolDiameterMm);
            if (op is null) continue;
            ops.Add(plan.Cuts.Count == 1
                ? op
                : op with { FeatureId = $"{FeatureId}-{i}" });
        }
        return ops;
    }

    static IReadOnlyList<(double X, double Y)> Overshoot(
        IReadOnlyList<(double X, double Y)> poly,
        double extraMm)
    {
        if (poly.Count < 2 || extraMm < 1e-9) return poly.ToList();
        var list = poly.ToList();
        list[0] = Extend(list[1], list[0], extraMm);
        list[^1] = Extend(list[^2], list[^1], extraMm);
        return list;
    }

    static (double X, double Y) Extend((double X, double Y) from, (double X, double Y) toward, double extra)
    {
        var dx = toward.X - from.X;
        var dy = toward.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return toward;
        return (toward.X + dx / len * extra, toward.Y + dy / len * extra);
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
