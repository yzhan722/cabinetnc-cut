namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>
/// Post-pass: pack smaller same-material parts into through-cutout voids of larger hosts.
/// Enabled per stock kind via <see cref="NestSheetSpec.AllowPartsInPart"/>.
/// </summary>
public static class PartsInPartPacker
{
    const double MinVoidMm = 20;

    public static NestResult Apply(
        NestResult primary,
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> stockTemplates,
        Func<Panel, (double w, double h)> sizeOf)
    {
        if (primary.Placements.Count == 0) return primary;
        if (!stockTemplates.Any(s => s.AllowPartsInPart)) return primary;

        var byId = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var pipKeys = stockTemplates
            .Where(s => s.AllowPartsInPart)
            .Select(s => NestGroupKey.From(s.Material, s.ThicknessMm))
            .ToHashSet();

        // If stock has PIP but blank material, allow any panel whose sheet template matched by thickness+material on used sheets.
        var placements = primary.Placements
            .Select(p => new NestPlacement
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                RotationDeg = p.RotationDeg,
            })
            .ToList();
        var placeById = placements.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var unplaced = primary.Unplaced.ToList();
        var slots = new List<PartInPartSlot>();
        var nestedChildren = new HashSet<string>(StringComparer.Ordinal);

        var voids = BuildVoids(placements, byId, pipKeys, settings.ClearanceMm);
        if (voids.Count == 0) return WithSlots(primary, slots);

        // Prefer emptying high sheet indices and consuming unplaced first.
        var candidateIds = new List<string>();
        candidateIds.AddRange(unplaced);
        candidateIds.AddRange(
            placements
                .OrderByDescending(p => p.SheetIndex)
                .ThenBy(p => AreaOf(p.PanelId, byId, sizeOf))
                .Select(p => p.PanelId));

        foreach (var voidSlot in voids.OrderByDescending(v => v.UsableW * v.UsableH))
        {
            if (!byId.TryGetValue(voidSlot.HostPanelId, out var host)) continue;
            if (!PipAllowedFor(host, pipKeys)) continue;

            var free = new List<Rect>
            {
                new(voidSlot.UsableMinX, voidSlot.UsableMinY, voidSlot.UsableW, voidSlot.UsableH),
            };
            var gap = Math.Max(0, settings.ClearanceMm);

            foreach (var childId in candidateIds)
            {
                if (nestedChildren.Contains(childId)) continue;
                if (childId == voidSlot.HostPanelId) continue;
                if (!byId.TryGetValue(childId, out var child)) continue;
                if (!SameStock(host, child)) continue;
                if (!PipAllowedFor(child, pipKeys)) continue;
                // Don't nest a host into its own smaller void chain for v1 if child also has huge voids — still OK if it fits.

                var (bw, bh) = sizeOf(child);
                if (bw <= 0 || bh <= 0) continue;
                if (bw * bh > voidSlot.UsableW * voidSlot.UsableH + 1e-9) continue;

                var mayRotate = settings.PanelMayRotate90(child);
                var orients = new List<(double w, double h, double rot)> { (bw, bh, 0) };
                if (mayRotate && Math.Abs(bw - bh) > 1e-6)
                    orients.Add((bh, bw, 90));

                (Rect fr, double w, double h, double rot)? best = null;
                foreach (var o in orients)
                {
                    foreach (var fr in free.OrderBy(r => r.Y).ThenBy(r => r.X))
                    {
                        if (o.w <= fr.W + 1e-9 && o.h <= fr.H + 1e-9)
                        {
                            if (best is null
                                || fr.Y < best.Value.fr.Y
                                || (Math.Abs(fr.Y - best.Value.fr.Y) < 1e-9 && fr.X < best.Value.fr.X))
                                best = (fr, o.w, o.h, o.rot);
                            break;
                        }
                    }
                }

                if (best is null) continue;
                var b = best.Value;
                var newPlace = new NestPlacement
                {
                    PanelId = childId,
                    SheetIndex = voidSlot.SheetIndex,
                    OffsetX = b.fr.X,
                    OffsetY = b.fr.Y,
                    RotationDeg = b.rot,
                };

                if (placeById.ContainsKey(childId))
                {
                    var idx = placements.FindIndex(p => p.PanelId == childId);
                    if (idx >= 0) placements[idx] = newPlace;
                    placeById[childId] = newPlace;
                }
                else
                {
                    placements.Add(newPlace);
                    placeById[childId] = newPlace;
                    unplaced.RemoveAll(id => id == childId);
                }

                nestedChildren.Add(childId);
                slots.Add(new PartInPartSlot
                {
                    HostPanelId = voidSlot.HostPanelId,
                    ChildPanelId = childId,
                    FeatureId = voidSlot.FeatureId,
                    SheetIndex = voidSlot.SheetIndex,
                    Enabled = true,
                });
                free = SplitFree(free, b.fr.X, b.fr.Y, b.w + gap, b.h + gap);
            }
        }

        if (slots.Count == 0) return WithSlots(primary, slots);

        var (compacted, sheetsUsed, sheetCount, sheetMap) = CompactSheets(placements, primary.SheetsUsed);
        foreach (var s in slots)
            s.SheetIndex = sheetMap.TryGetValue(s.SheetIndex, out var neu) ? neu : s.SheetIndex;

        return new NestResult
        {
            Engine = primary.Engine,
            Placements = compacted,
            SheetCount = sheetCount,
            Unplaced = unplaced,
            UnplacedReasons = primary.UnplacedReasons
                .Where(r => !nestedChildren.Contains(r.PanelId))
                .ToList(),
            GroupReports = primary.GroupReports,
            SheetsUsed = sheetsUsed,
            PartInPartSlots = slots,
        };
    }

    public static HashSet<(string A, string B)> IgnoreCollisionPairs(IEnumerable<PartInPartSlot> slots)
    {
        var set = new HashSet<(string, string)>();
        foreach (var s in slots)
        {
            if (!s.Enabled) continue;
            set.Add((s.HostPanelId, s.ChildPanelId));
            set.Add((s.ChildPanelId, s.HostPanelId));
        }
        return set;
    }

    static NestResult WithSlots(NestResult primary, IReadOnlyList<PartInPartSlot> slots) =>
        new()
        {
            Engine = primary.Engine,
            Placements = primary.Placements,
            SheetCount = primary.SheetCount,
            Unplaced = primary.Unplaced,
            UnplacedReasons = primary.UnplacedReasons,
            GroupReports = primary.GroupReports,
            SheetsUsed = primary.SheetsUsed,
            PartInPartSlots = slots,
        };

    static List<VoidRegion> BuildVoids(
        IReadOnlyList<NestPlacement> placements,
        IReadOnlyDictionary<string, Panel> byId,
        HashSet<NestGroupKey> pipKeys,
        double clearanceMm)
    {
        var voids = new List<VoidRegion>();
        var inset = Math.Max(0, clearanceMm);
        foreach (var place in placements)
        {
            if (!byId.TryGetValue(place.PanelId, out var host)) continue;
            if (!PipAllowedFor(host, pipKeys)) continue;
            var hostBounds = NestTransform.BoundsOf(host);
            foreach (var f in host.Features)
            {
                if (!PanelEdit.IsCutout(f)) continue;
                if (!f.Through) continue;
                var ring = f.Path ?? f.Profile;
                if (ring is not { Count: >= 3 }) continue;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var pt in ring)
                {
                    var (sx, sy) = NestTransform.ToSheet(
                        pt.X, pt.Y, hostBounds,
                        place.OffsetX, place.OffsetY, place.RotationDeg);
                    minX = Math.Min(minX, sx);
                    minY = Math.Min(minY, sy);
                    maxX = Math.Max(maxX, sx);
                    maxY = Math.Max(maxY, sy);
                }

                var usableMinX = minX + inset;
                var usableMinY = minY + inset;
                var usableMaxX = maxX - inset;
                var usableMaxY = maxY - inset;
                var uw = usableMaxX - usableMinX;
                var uh = usableMaxY - usableMinY;
                if (uw < MinVoidMm || uh < MinVoidMm) continue;

                voids.Add(new VoidRegion(
                    place.PanelId,
                    f.FeatureId,
                    place.SheetIndex,
                    usableMinX,
                    usableMinY,
                    uw,
                    uh));
            }
        }
        return voids;
    }

    static bool PipAllowedFor(Panel panel, HashSet<NestGroupKey> pipKeys)
    {
        if (pipKeys.Count == 0) return false;
        var key = NestGroupKey.From(panel.Material, panel.ThicknessMm);
        if (pipKeys.Contains(key)) return true;
        // Templates sometimes omit material; match thickness-only PIP flags.
        return pipKeys.Any(k =>
            Math.Abs(k.ThicknessMm - key.ThicknessMm) < 1e-6
            && (k.Material == "(unspecified)"
                || string.Equals(k.Material, key.Material, StringComparison.OrdinalIgnoreCase)));
    }

    static bool SameStock(Panel a, Panel b) =>
        NestGroupKey.From(a.Material, a.ThicknessMm)
            .Equals(NestGroupKey.From(b.Material, b.ThicknessMm));

    static double AreaOf(string id, IReadOnlyDictionary<string, Panel> byId, Func<Panel, (double w, double h)> sizeOf)
    {
        if (!byId.TryGetValue(id, out var p)) return 0;
        var (w, h) = sizeOf(p);
        return w * h;
    }

    static List<Rect> SplitFree(List<Rect> free, double x, double y, double w, double h)
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
        return next.Where(a => a.W >= 1 && a.H >= 1).Where((a, i) =>
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

    static (List<NestPlacement> Placements, IReadOnlyList<NestSheetSpec> Sheets, int SheetCount, Dictionary<int, int> Map)
        CompactSheets(List<NestPlacement> placements, IReadOnlyList<NestSheetSpec> sheetsUsed)
    {
        if (placements.Count == 0)
            return ([], sheetsUsed, 0, new Dictionary<int, int>());

        var used = placements.Select(p => p.SheetIndex).Distinct().OrderBy(i => i).ToList();
        var map = used.Select((old, neu) => (old, neu)).ToDictionary(t => t.old, t => t.neu);
        var compacted = placements.Select(p => new NestPlacement
        {
            PanelId = p.PanelId,
            SheetIndex = map[p.SheetIndex],
            OffsetX = p.OffsetX,
            OffsetY = p.OffsetY,
            RotationDeg = p.RotationDeg,
        }).ToList();

        var sheets = new List<NestSheetSpec>();
        foreach (var old in used)
        {
            if (old >= 0 && old < sheetsUsed.Count)
                sheets.Add(sheetsUsed[old]);
            else if (sheetsUsed.Count > 0)
                sheets.Add(sheetsUsed[^1]);
        }

        return (compacted, sheets, used.Count, map);
    }

    readonly record struct VoidRegion(
        string HostPanelId,
        string FeatureId,
        int SheetIndex,
        double UsableMinX,
        double UsableMinY,
        double UsableW,
        double UsableH);

    readonly record struct Rect(double X, double Y, double W, double H);
}
