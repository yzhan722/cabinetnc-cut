using CabinetNC.Cloud.Contracts;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Desktop.Core.Cloud;

/// <summary>Something the Desktop knows that the rectangular cloud contract cannot express.</summary>
public sealed record NestContractDowngrade(string Code, string Message);

public sealed record NestContractRequest(SubmitNestJobRequest Request, IReadOnlyList<NestContractDowngrade> Downgrades);

/// <summary>
/// Reduces the Desktop's nest inputs to the shared runner's rectangular contract (AABB parts, one stock
/// size, uniform border) and lists every simplification so the UI can show it and the acceptance
/// document can state it. The Local NFP path is untouched by this.
/// </summary>
public static class NestRequestBuilder
{
    public static NestContractRequest Build(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> sheets,
        Func<Panel, (double w, double h)> sizeOf)
    {
        var downgrades = new List<NestContractDowngrade>();

        var primary = sheets.Count > 0 ? sheets[0] : null;
        if (primary is null)
            downgrades.Add(new("default_sheet", "没有可用大板规格，内网排版使用默认 1220 × 2440。"));
        var sheetWidth = primary?.WidthMm ?? 1220;
        var sheetLength = primary?.LengthMm ?? 2440;

        if (sheets.Count > 1)
        {
            downgrades.Add(new("extra_sheets_ignored",
                $"内网排版只使用第一种大板 {sheetWidth:0.#} × {sheetLength:0.#}；其余 {sheets.Count - 1} 种（余料/其他材料）未参与。"));
        }
        if (sheets.Any(s => s.Blocked.Count > 0))
            downgrades.Add(new("keepouts_ignored", "内网排版忽略大板上的缺陷禁排区。"));
        if (primary is not null && (primary.InsetLeftMm ?? primary.InsetBottomMm ?? primary.InsetRightMm ?? primary.InsetTopMm) is not null)
            downgrades.Add(new("insets_ignored", "内网排版使用统一边距，忽略按边设置的余料边距。"));
        if (sheets.Any(s => s.AllowPartsInPart))
            downgrades.Add(new("parts_in_part_disabled", "内网排版不支持 parts in part（子件嵌入开窗）。"));

        var nonRect = 0;
        var parts = new List<NestPartDto>(panels.Count);
        foreach (var panel in panels)
        {
            if (!IsAxisAlignedRectangle(panel.Outline))
                nonRect++;
            var (w, h) = sizeOf(panel);
            parts.Add(new NestPartDto(panel.PanelId, w, h, settings.PanelMayRotate90(panel), panel.Material, panel.ThicknessMm));
        }
        if (nonRect > 0)
            downgrades.Add(new("true_shape_to_aabb", $"{nonRect} 件异形板按外接矩形排版（本机精排会按真实轮廓）。"));

        var request = new SubmitNestJobRequest(parts, sheetWidth, sheetLength, settings.ClearanceMm, settings.MarginMm, settings.AllowRotation);
        return new NestContractRequest(request, downgrades);
    }

    static bool IsAxisAlignedRectangle(Outline outline)
    {
        var points = outline.Points;
        if (points.Count == 5 && points[0] == points[4])
            points = points.Take(4).ToList();
        if (points.Count != 4)
            return false;
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var horizontal = Math.Abs(a.Y - b.Y) < 1e-6;
            var vertical = Math.Abs(a.X - b.X) < 1e-6;
            if (horizontal == vertical)
                return false;
        }
        return true;
    }
}

/// <summary>Turns a server result into the objects the Desktop already renders and validates.</summary>
public static class NestResultMapper
{
    public static (NestResult Result, NestEngineRunLog Log) ToLocal(NestJobResult result, NestSheetSpec sheet)
    {
        var placements = result.Placements
            .Select(p => new NestPlacement { PanelId = p.PanelId, SheetIndex = p.SheetIndex, OffsetX = p.OffsetX, OffsetY = p.OffsetY, RotationDeg = p.RotationDeg })
            .ToList();
        var highestSheet = placements.Count == 0 ? 0 : placements.Max(p => p.SheetIndex) + 1;
        var sheetCount = Math.Max(result.SheetCount, highestSheet);

        var local = new NestResult
        {
            Engine = result.Engine,
            Placements = placements,
            SheetCount = sheetCount,
            Unplaced = result.Unplaced.ToList(),
            SheetsUsed = Enumerable.Repeat(sheet, sheetCount).ToList(),
        };
        var log = new NestEngineRunLog
        {
            SelectedEngine = result.Engine,
            AttemptedEngine = "intranet",
            ElapsedMs = result.DurationMs,
        };
        return (local, log);
    }
}
