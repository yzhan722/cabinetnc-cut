namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>Axis-aligned extent of a panel outline; the size every nesting contract and gate agrees on.</summary>
public static class PanelExtents
{
    public static (double w, double h) SizeOfOutline(Panel panel)
    {
        var pts = panel.Outline.Points;
        if (pts.Count == 0) return (0, 0);
        return (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));
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

        var panelMap = panels.ToDictionary(p => p.PanelId, p => p);
        foreach (var id in placements
                     .Select(p => p.PanelId)
                     .Where(id => !panelMap.ContainsKey(id))
                     .Distinct(StringComparer.Ordinal))
        {
            errors.Add($"unknown_panel: placement references {id}");
        }

        foreach (var duplicate in placements
                     .GroupBy(p => p.PanelId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            errors.Add($"duplicate_placement: {duplicate.Key} ×{duplicate.Count()}");
        }

        if (requirePlacements)
        {
            var placedIds = placements.Select(p => p.PanelId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in panels
                         .Select(p => p.PanelId)
                         .Where(id => !placedIds.Contains(id))
                         .Distinct(StringComparer.Ordinal))
            {
                errors.Add($"unplaced_panel: {id}");
            }
        }

        var parts = panels.Select(p =>
        {
            var (w, h) = PanelExtents.SizeOfOutline(p);
            return new NestPart { PanelId = p.PanelId, WidthMm = w, HeightMm = h };
        }).ToList();

        var ignore = partInPartSlots is { Count: > 0 }
            ? PartsInPartGeometry.IgnoreCollisionPairs(partInPartSlots)
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
