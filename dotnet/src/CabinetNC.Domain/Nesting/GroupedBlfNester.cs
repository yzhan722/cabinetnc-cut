namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>
/// Packs by Material + ThicknessMm so mixed stock never shares a sheet.
/// Uses BLF per group (AABB engine — not NFP).
/// </summary>
public static class GroupedBlfNester
{
    public static NestResult Pack(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null)
    {
        var groups = panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();

        var placements = new List<NestPlacement>();
        var unplaced = new List<string>();
        var reasons = new List<NestUnplacedReason>();
        var groupReports = new List<NestGroupReport>();
        var sheetCursor = 0;
        var sheetMeta = new List<NestSheetSpec>();
        var totalPanels = Math.Max(1, panels.Count);
        var placedCursor = 0;

        for (var gi = 0; gi < groups.Count; gi++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[gi];
            var key = group.Key;
            var groupPanels = group.ToList();
            progress?.Report(new NestProgressReport
            {
                Done = placedCursor,
                Total = totalPanels,
                Message = $"BLF · {key} ({gi + 1}/{groups.Count})",
            });
            var matched = MatchSheets(stockTemplates, key);
            if (matched.Count == 0)
            {
                foreach (var p in groupPanels)
                {
                    unplaced.Add(p.PanelId);
                    reasons.Add(new NestUnplacedReason
                    {
                        PanelId = p.PanelId,
                        Code = "no_stock_for_group",
                        Message = $"无匹配板材：{key}",
                    });
                }
                groupReports.Add(new NestGroupReport
                {
                    Key = key,
                    PartCount = groupPanels.Count,
                    PlacedCount = 0,
                    SheetCount = 0,
                    LocalSheetStart = sheetCursor,
                });
                placedCursor += groupPanels.Count;
                continue;
            }

            var stock = matched[0];
            var groupSettings = NestStockOverrides.ForGroup(settings, stock);
            var parts = groupPanels.Select(p =>
            {
                var (w, h) = sizeOf(p);
                return new NestPart
                {
                    PanelId = p.PanelId,
                    WidthMm = w,
                    HeightMm = h,
                    MayRotate = groupSettings.PanelMayRotate90(p),
                    Material = key.Material,
                    ThicknessMm = key.ThicknessMm,
                };
            }).ToList();

            var packed = BlfNester.Pack(new NestRequest
            {
                Parts = parts,
                SpacingMm = groupSettings.ClearanceMm,
                BorderMm = groupSettings.MarginMm,
                AllowRotation = groupSettings.AllowRotation,
                Sheets = matched,
                SheetWidthMm = matched[0].WidthMm,
                SheetLengthMm = matched[0].LengthMm,
            });

            var localStart = sheetCursor;
            var maxLocal = packed.Placements.Count == 0
                ? -1
                : packed.Placements.Max(p => p.SheetIndex);

            // Materialize sheet queue used by BLF (including infinite clones)
            for (var i = 0; i <= maxLocal; i++)
            {
                var src = i < matched.Count ? matched[i] : CloneTemplate(matched[^1], key);
                sheetMeta.Add(WithGroup(src, key));
            }
            if (maxLocal >= 0)
                sheetCursor += maxLocal + 1;

            foreach (var place in packed.Placements)
            {
                placements.Add(new NestPlacement
                {
                    PanelId = place.PanelId,
                    SheetIndex = localStart + place.SheetIndex,
                    OffsetX = place.OffsetX,
                    OffsetY = place.OffsetY,
                    RotationDeg = place.RotationDeg,
                });
            }

            foreach (var id in packed.Unplaced)
            {
                unplaced.Add(id);
                reasons.Add(new NestUnplacedReason
                {
                    PanelId = id,
                    Code = "does_not_fit",
                    Message = $"组 {key} 内无法放入（尺寸/缺陷/边距）",
                });
            }

            double used = 0;
            var placedIds = packed.Placements.Select(p => p.PanelId).ToHashSet();
            foreach (var part in parts.Where(p => placedIds.Contains(p.PanelId)))
                used += part.WidthMm * part.HeightMm;
            var sheetCount = maxLocal < 0 ? 0 : maxLocal + 1;
            double sheetArea = 0;
            for (var i = 0; i < sheetCount; i++)
            {
                var s = sheetMeta[localStart + i];
                sheetArea += s.WidthMm * s.LengthMm;
            }

            groupReports.Add(new NestGroupReport
            {
                Key = key,
                PartCount = groupPanels.Count,
                PlacedCount = packed.Placements.Count,
                SheetCount = sheetCount,
                LocalSheetStart = localStart,
                UtilizationPct = sheetArea > 0 ? used / sheetArea * 100 : 0,
            });
            placedCursor += groupPanels.Count;
            progress?.Report(new NestProgressReport
            {
                Done = placedCursor,
                Total = totalPanels,
                Message = $"BLF · {key} 完成 · {packed.Placements.Count}/{groupPanels.Count}",
            });
        }

        return new NestResult
        {
            Engine = "grouped_blf_v0",
            Placements = placements,
            SheetCount = sheetMeta.Count,
            Unplaced = unplaced,
            UnplacedReasons = reasons,
            GroupReports = groupReports,
            SheetsUsed = sheetMeta,
        };
    }

    /// <summary>
    /// Match stock: exact material+thickness; blank material templates match thickness only
    /// (ASSUMPTION: UI default stock without material is a fallback template).
    /// </summary>
    public static List<NestSheetSpec> MatchSheets(IReadOnlyList<NestSheetSpec> templates, NestGroupKey key)
    {
        var exact = templates.Where(s =>
            NestGroupKey.From(s.Material, s.ThicknessMm).Equals(key)).ToList();
        if (exact.Count > 0) return exact;

        var byThickness = templates.Where(s =>
            string.IsNullOrWhiteSpace(s.Material) &&
            Math.Abs(Math.Round(s.ThicknessMm, 2) - key.ThicknessMm) < 1e-6).ToList();
        if (byThickness.Count > 0)
            return byThickness.Select(s => WithGroup(s, key)).ToList();

        // Thickness-agnostic blank stock (ThicknessMm == 0) — clone per group
        var blank = templates.Where(s =>
            string.IsNullOrWhiteSpace(s.Material) && s.ThicknessMm <= 0).ToList();
        if (blank.Count > 0)
            return blank.Select(s => WithGroup(s, key)).ToList();

        return [];
    }

    static NestSheetSpec WithGroup(NestSheetSpec s, NestGroupKey key) =>
        new()
        {
            WidthMm = s.WidthMm,
            LengthMm = s.LengthMm,
            BorderMm = s.BorderMm,
            SpacingMm = s.SpacingMm,
            AllowRotation = s.AllowRotation,
            AllowPartsInPart = s.AllowPartsInPart,
            Blocked = s.Blocked,
            Label = string.IsNullOrWhiteSpace(s.Label)
                ? $"{key.Material}_{key.ThicknessMm:0.##}"
                : $"{s.Label}|{key.Material}_{key.ThicknessMm:0.##}",
            Material = key.Material,
            ThicknessMm = key.ThicknessMm,
        };

    static NestSheetSpec CloneTemplate(NestSheetSpec s, NestGroupKey key) =>
        WithGroup(new NestSheetSpec
        {
            WidthMm = s.WidthMm,
            LengthMm = s.LengthMm,
            BorderMm = s.BorderMm,
            SpacingMm = s.SpacingMm,
            AllowRotation = s.AllowRotation,
            AllowPartsInPart = s.AllowPartsInPart,
            Blocked = [],
            Label = s.Label,
            Material = key.Material,
            ThicknessMm = key.ThicknessMm,
        }, key);

    public static (double w, double h) SizeOfOutline(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count == 0) return (0, 0);
        var minX = pts.Min(p => p.X);
        var minY = pts.Min(p => p.Y);
        var maxX = pts.Max(p => p.X);
        var maxY = pts.Max(p => p.Y);
        return (maxX - minX, maxY - minY);
    }
}

/// <summary>Export hard-gate for nest polygon/AABB spacing (Day 5).</summary>
public static class NestExportGate
{
    public static (bool Ok, IReadOnlyList<string> Errors) Check(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        double clearanceMm,
        bool requirePlacements = true,
        bool allowAabbOverlap = false,
        IReadOnlyList<PartInPartSlot>? partInPartSlots = null)
    {
        var errors = new List<string>();
        if (requirePlacements && placements.Count == 0)
            errors.Add("nest_empty: 无排版结果");

        var parts = panels.Select(p =>
        {
            var (w, h) = GroupedBlfNester.SizeOfOutline(p);
            return new NestPart { PanelId = p.PanelId, WidthMm = w, HeightMm = h };
        }).ToList();

        var ignore = partInPartSlots is { Count: > 0 }
            ? PartsInPartPacker.IgnoreCollisionPairs(partInPartSlots)
            : null;

        // True-shape engines may intentionally overlap bounding boxes while the
        // actual polygons remain safely separated.
        if (!allowAabbOverlap)
        {
            foreach (var hit in NestValidator.FindAabbCollisions(parts, placements, clearanceMm, ignore))
                errors.Add($"aabb_gap: {hit.PanelIdA} × {hit.PanelIdB} · S{hit.SheetIndex + 1}");
        }

        foreach (var hit in NestValidator.FindPolygonCollisions(panels, placements, clearanceMm, ignore))
            errors.Add($"poly_gap: {hit.PanelIdA} × {hit.PanelIdB} · S{hit.SheetIndex + 1}");

        // Mixed material/thickness on same sheet index = hard fail
        var bySheet = placements.GroupBy(p => p.SheetIndex);
        var panelMap = panels.ToDictionary(p => p.PanelId, p => p);
        foreach (var sheet in bySheet)
        {
            var keys = sheet
                .Select(p => panelMap.TryGetValue(p.PanelId, out var panel)
                    ? NestGroupKey.From(panel.Material, panel.ThicknessMm)
                    : (NestGroupKey?)null)
                .Where(k => k is not null)
                .Select(k => k!.Value)
                .Distinct()
                .ToList();
            if (keys.Count > 1)
                errors.Add($"mixed_group_sheet: S{sheet.Key + 1} contains {string.Join(" | ", keys)}");
        }

        return (errors.Count == 0, errors);
    }
}
