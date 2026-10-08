namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Geometry;

/// <summary>A tab on a profile toolpath. Paired bridges share each other's <see cref="PairId"/>.</summary>
public sealed record ProfileBridge
{
    public required string Id { get; init; }
    public required string PanelId { get; init; }
    public string? FeatureId { get; init; }
    public int SheetIndex { get; init; }
    public double ArcLengthMm { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double WidthMm { get; init; }
    public string? PairId { get; init; }
}

public sealed record BridgeClickResult(
    IReadOnlyList<ProfileBridge> Bridges,
    string Message,
    bool Changed);

/// <summary>
/// Manual profile tabs. Adjacent panels whose <b>outline gap</b> at the click
/// is less than 2× tool diameter get a mandatory paired tab; otherwise a single tab.
/// </summary>
public static class ProfileBridgePlanner
{
    public const int MaxPerPanel = 100;
    /// <summary>Remaining web in the middle of the tab. Tool-centre skip adds 2× radius.</summary>
    public const double DefaultWidthMm = 5;

    /// <summary>
    /// Shop width is the leftover web. Tool centre must lift across
    /// <paramref name="webMm"/> plus one diameter (two radii) so the
    /// through-cuts on each side do not eat the tab.
    /// </summary>
    public static double ToolCenterSpanMm(double webMm, double toolDiameterMm) =>
        Math.Max(0, webMm) + Math.Max(0, toolDiameterMm);
    /// <summary>Long/short &gt; this counts as a strip.</summary>
    public const double StripAspect = 12;
    public const double TinyAreaM2 = 0.1;
    public const double LargeAreaM2 = 0.15;
    public const double MiddleSpacingMm = 500;
    /// <summary>Short side &gt; this and &lt; <see cref="StripOnePairMm"/>: two even left/right pairs.</summary>
    public const double StripTwoPairMinMm = 100;
    /// <summary>Short side ≥ this: one left/right pair at mid-length.</summary>
    public const double StripOnePairMm = 125;
    public const double EndInsetMm = 50;
    public const double DedupMm = 28;
    public const double CornerSkipMm = 18;
    public const double SameEdgeMergeMm = 300;

    public static double PairClearanceLimitMm(double toolDiameterMm) =>
        Math.Max(0, toolDiameterMm) * 2;

    public static BridgeClickResult HandleClick(
        IReadOnlyList<ProfileBridge> existing,
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlinesByPanel,
        int sheetIndex,
        double x,
        double y,
        double toolDiameterMm,
        double widthMm,
        double hitTolMm,
        double? symbolTolMm = null)
    {
        var list = existing.ToList();
        var width = widthMm > 0.05 ? widthMm : DefaultWidthMm;
        var symbolTol = symbolTolMm ?? Math.Max(3, hitTolMm * 0.4);

        var existingMark = NearestSymbol(list, sheetIndex, x, y);
        if (existingMark is not null && existingMark.Dist <= symbolTol)
            return new BridgeClickResult(list, "此处已有桥 · 请用删除桥去掉", false);

        var contours = ContoursOnSheet(ops, sheetIndex);
        if (contours.Count == 0)
            return new BridgeClickResult(list, "请先计算刀路", false);

        CutOp? bestOp = null;
        PolylineQuery.Hit? bestHit = null;
        foreach (var op in contours)
        {
            if (op.Path is not { Count: >= 2 } path) continue;
            var hit = PolylineQuery.Nearest(path, x, y, op.ClosePath);
            if (hit is null) continue;
            if (bestHit is null || hit.Value.Distance < bestHit.Value.Distance)
            {
                bestHit = hit;
                bestOp = op;
            }
        }

        if (bestOp is null || bestHit is null || bestHit.Value.Distance > hitTolMm)
            return new BridgeClickResult(list, "点外形刀路轨迹放桥", false);

        var hitV = bestHit.Value;
        var duplicate = NearestSymbolOnPanel(list, bestOp.PanelId, bestOp.FeatureId, hitV.X, hitV.Y);
        if (duplicate is not null && duplicate.Dist <= 1.2)
            return new BridgeClickResult(list, "此处已有桥", false);

        if (CountOnPanel(list, bestOp.PanelId) >= MaxPerPanel)
            return new BridgeClickResult(list, $"该板已有 {MaxPerPanel} 个桥", false);

        var pairLimit = PairClearanceLimitMm(toolDiameterMm);
        var neighbor = FindForcedNeighbor(
            bestOp.PanelId,
            new Point2(hitV.X, hitV.Y),
            outlinesByPanel,
            pairLimit);

        if (neighbor is { } nb)
        {
            var pairOp = contours.FirstOrDefault(o =>
                o.PanelId == nb.PanelId && o.FeatureId is null && o.Path is { Count: >= 2 });
            if (pairOp?.Path is { Count: >= 2 } pairPath)
            {
                if (CountOnPanel(list, nb.PanelId) >= MaxPerPanel)
                    return new BridgeClickResult(list, $"对面板已有 {MaxPerPanel} 个桥，无法成对", false);

                var pairHit = PolylineQuery.Nearest(pairPath, nb.Point.X, nb.Point.Y, pairOp.ClosePath);
                if (pairHit is not null)
                {
                    var idA = NewId();
                    var idB = NewId();
                    list.Add(new ProfileBridge
                    {
                        Id = idA,
                        PanelId = bestOp.PanelId,
                        FeatureId = bestOp.FeatureId,
                        SheetIndex = sheetIndex,
                        ArcLengthMm = hitV.ArcLengthMm,
                        X = hitV.X,
                        Y = hitV.Y,
                        WidthMm = width,
                        PairId = idB,
                    });
                    list.Add(new ProfileBridge
                    {
                        Id = idB,
                        PanelId = pairOp.PanelId,
                        FeatureId = pairOp.FeatureId,
                        SheetIndex = sheetIndex,
                        ArcLengthMm = pairHit.Value.ArcLengthMm,
                        X = pairHit.Value.X,
                        Y = pairHit.Value.Y,
                        WidthMm = width,
                        PairId = idA,
                    });
                    return new BridgeClickResult(list, "已放成对桥", true);
                }
            }
        }

        list.Add(new ProfileBridge
        {
            Id = NewId(),
            PanelId = bestOp.PanelId,
            FeatureId = bestOp.FeatureId,
            SheetIndex = sheetIndex,
            ArcLengthMm = hitV.ArcLengthMm,
            X = hitV.X,
            Y = hitV.Y,
            WidthMm = width,
            PairId = null,
        });
        return new BridgeClickResult(list, "已放桥", true);
    }

    public static BridgeClickResult HandleDelete(
        IReadOnlyList<ProfileBridge> existing,
        int sheetIndex,
        double x,
        double y,
        double symbolTolMm)
    {
        var list = existing.ToList();
        var symbol = NearestSymbol(list, sheetIndex, x, y);
        if (symbol is null || symbol.Dist > symbolTolMm)
            return new BridgeClickResult(list, "点桥标记删除", false);

        var removed = RemoveWithPair(list, symbol.Bridge.Id);
        return new BridgeClickResult(list, removed > 1 ? "已删除成对桥" : "已删除桥", true);
    }

    public static BridgeClickResult ClearSheet(IReadOnlyList<ProfileBridge> existing, int sheetIndex)
    {
        var kept = existing.Where(b => b.SheetIndex != sheetIndex).ToList();
        var n = existing.Count - kept.Count;
        return new BridgeClickResult(
            kept,
            n == 0 ? "本页没有桥" : $"已清空本页 {n} 个桥",
            n > 0);
    }

    public static IReadOnlyList<ProfileBridge> Reproject(
        IReadOnlyList<ProfileBridge> bridges,
        IReadOnlyList<CutOp> ops)
    {
        if (bridges.Count == 0) return bridges;
        var result = new List<ProfileBridge>(bridges.Count);
        var representedSheets = ops
            .Where(o => o.Placed && o.Op == "contour" && o.Path is { Count: >= 2 })
            .Select(o => o.SheetIndex)
            .ToHashSet();
        foreach (var b in bridges)
        {
            // A single-sheet CAM refresh must not delete bridges belonging to
            // other sheets that are intentionally absent from this ops scope.
            if (!representedSheets.Contains(b.SheetIndex))
            {
                result.Add(b);
                continue;
            }
            var op = ops.FirstOrDefault(o =>
                o.Placed
                && o.SheetIndex == b.SheetIndex
                && o.Op == "contour"
                && o.PanelId == b.PanelId
                && string.Equals(o.FeatureId, b.FeatureId, StringComparison.Ordinal)
                && o.Path is { Count: >= 2 });
            if (op?.Path is not { Count: >= 2 } path)
                continue;
            var hit = PolylineQuery.Nearest(path, b.X, b.Y, op.ClosePath)
                      ?? PolylineQuery.Nearest(path, b.X, b.Y, true);
            if (hit is null) continue;
            result.Add(b with
            {
                ArcLengthMm = hit.Value.ArcLengthMm,
                X = hit.Value.X,
                Y = hit.Value.Y,
            });
        }

        var keep = new HashSet<string>(result.Select(x => x.Id), StringComparer.Ordinal);
        return result
            .Select(b => b.PairId is not null && keep.Contains(b.PairId) ? b : b with { PairId = null })
            .ToList();
    }

    static List<CutOp> ContoursOnSheet(IReadOnlyList<CutOp> ops, int sheetIndex) =>
        ops.Where(o =>
                o.Placed
                && o.SheetIndex == sheetIndex
                && o.Op == "contour"
                && o.Path is { Count: >= 2 })
            .ToList();

    static int CountOnPanel(IReadOnlyList<ProfileBridge> list, string panelId) =>
        list.Count(b => b.PanelId == panelId);

    static int RemoveWithPair(List<ProfileBridge> list, string id)
    {
        var target = list.FirstOrDefault(b => b.Id == id);
        if (target is null) return 0;
        var pair = target.PairId;
        var n = list.RemoveAll(b => b.Id == id || (pair is not null && b.Id == pair));
        return n;
    }

    sealed record SymbolHit(ProfileBridge Bridge, double Dist);

    static SymbolHit? NearestSymbol(IReadOnlyList<ProfileBridge> list, int sheetIndex, double x, double y)
    {
        SymbolHit? best = null;
        foreach (var b in list.Where(b => b.SheetIndex == sheetIndex))
        {
            var dx = b.X - x;
            var dy = b.Y - y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (best is null || d < best.Dist)
                best = new SymbolHit(b, d);
        }
        return best;
    }

    static SymbolHit? NearestSymbolOnPanel(
        IReadOnlyList<ProfileBridge> list, string panelId, string? featureId, double x, double y)
    {
        SymbolHit? best = null;
        foreach (var b in list.Where(b =>
                     b.PanelId == panelId
                     && string.Equals(b.FeatureId, featureId, StringComparison.Ordinal)))
        {
            var dx = b.X - x;
            var dy = b.Y - y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (best is null || d < best.Dist)
                best = new SymbolHit(b, d);
        }
        return best;
    }

    /// <summary>
    /// Local outline-to-outline gap at the click, measured in the outward half-plane.
    /// Pairing is forced when that gap is &lt; 2× tool diameter.
    /// </summary>
    public static (string PanelId, Point2 Point, double Dist)? FindForcedNeighbor(
        string panelId,
        Point2 toolpathPoint,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlinesByPanel,
        double maxGapMm)
    {
        if (!outlinesByPanel.TryGetValue(panelId, out var self) || self.Count < 2)
            return null;

        var (onSelf, _) = PolygonDistance.ClosestPoint(self, toolpathPoint);
        var outward = PolygonDistance.OutwardUnitNormal(self, onSelf);

        string? bestId = null;
        var bestPt = default(Point2);
        var bestD = double.PositiveInfinity;
        foreach (var (id, ring) in outlinesByPanel)
        {
            if (id == panelId || ring.Count < 2) continue;
            var (q, d) = PolygonDistance.ClosestPoint(ring, onSelf);
            if (double.IsNaN(d) || d >= maxGapMm || d >= bestD) continue;
            if (d > 1e-6)
            {
                var vx = q.X - onSelf.X;
                var vy = q.Y - onSelf.Y;
                var dot = vx * outward.X + vy * outward.Y;
                if (dot <= 0) continue;
            }

            bestD = d;
            bestId = id;
            bestPt = q;
        }

        return bestId is null ? null : (bestId, bestPt, bestD);
    }

    public static bool IsLongStrip(double widthMm, double heightMm, double stripAspect = StripAspect)
    {
        var longS = Math.Max(widthMm, heightMm);
        var shortS = Math.Max(1e-6, Math.Min(widthMm, heightMm));
        return longS / shortS > Math.Clamp(stripAspect, 1.5, 50);
    }

    public static double AreaM2(double widthMm, double heightMm) =>
        Math.Max(0, widthMm) * Math.Max(0, heightMm) / 1_000_000.0;

    public static bool IsSmallBoard(double widthMm, double heightMm, double largeAreaM2 = LargeAreaM2) =>
        AreaM2(widthMm, heightMm) <= largeAreaM2;

    public static bool IsLargeBoard(
        double widthMm, double heightMm,
        double largeAreaM2 = LargeAreaM2,
        double stripAspect = StripAspect) =>
        AreaM2(widthMm, heightMm) > largeAreaM2 && !IsLongStrip(widthMm, heightMm, stripAspect);

    public static double NormalizeStripAspect(double stripAspect) =>
        Math.Clamp(stripAspect, 1.5, 50);

    public static int TargetSmallBridges(double areaM2, double tinyAreaM2 = TinyAreaM2) =>
        areaM2 < tinyAreaM2 ? 3 : 2;

    public static (double Tiny, double Large) NormalizeAreaLimits(double tinyM2, double largeM2)
    {
        tinyM2 = Math.Clamp(tinyM2, 0.01, 5);
        largeM2 = Math.Clamp(largeM2, 0.01, 5);
        if (tinyM2 > largeM2)
            (tinyM2, largeM2) = (largeM2, tinyM2);
        return (tinyM2, largeM2);
    }

    /// <summary>
    /// Shop auto layout for the current sheet. Replaces bridges on that sheet.
    /// 1) small+strip, 2) pure strips (same-edge merge 300 mm), 3) remaining small.
    /// Facing a large board stays unidirectional.
    /// </summary>
    public static BridgeClickResult AutoPlace(
        IReadOnlyList<ProfileBridge> existing,
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlinesByPanel,
        int sheetIndex,
        double toolDiameterMm,
        double widthMm,
        double tinyAreaM2 = TinyAreaM2,
        double largeAreaM2 = LargeAreaM2,
        double stripAspect = StripAspect)
    {
        (tinyAreaM2, largeAreaM2) = NormalizeAreaLimits(tinyAreaM2, largeAreaM2);
        stripAspect = NormalizeStripAspect(stripAspect);
        var contours = ContoursOnSheet(ops, sheetIndex)
            .Where(o => o.FeatureId is null && o.Path is { Count: >= 2 })
            .ToList();
        if (contours.Count == 0)
            return new BridgeClickResult(existing.ToList(), "请先计算刀路", false);

        var width = widthMm > 0.05 ? widthMm : DefaultWidthMm;
        var pairLimit = PairClearanceLimitMm(toolDiameterMm);
        var list = existing.Where(b => b.SheetIndex != sheetIndex).ToList();

        var panels = new List<PanelShape>();
        foreach (var op in contours)
        {
            if (!outlinesByPanel.TryGetValue(op.PanelId, out var ring) || ring.Count < 3)
                continue;
            var b = BoundsOf(ring);
            panels.Add(new PanelShape(op.PanelId, op, ring, b, largeAreaM2, stripAspect));
        }

        bool ShouldPair(string neighborId)
        {
            var nb = panels.FirstOrDefault(p => p.PanelId == neighborId);
            return nb is null || !nb.IsLarge;
        }

        foreach (var p in panels.Where(p => p.IsSmall && p.IsStrip))
            PlaceStrip(list, contours, outlinesByPanel, p, sheetIndex, toolDiameterMm, width, 0, ShouldPair);

        foreach (var p in panels.Where(p => p.IsStrip && !p.IsSmall))
            PlaceStrip(list, contours, outlinesByPanel, p, sheetIndex, toolDiameterMm, width,
                SameEdgeMergeMm, ShouldPair);

        foreach (var p in panels.Where(p => p.IsSmall && !p.IsStrip))
            PlaceSmall(list, contours, outlinesByPanel, p, panels, sheetIndex, toolDiameterMm, width,
                pairLimit, tinyAreaM2, ShouldPair);

        list = EnsureFacingPairs(list, contours, outlinesByPanel, toolDiameterMm).ToList();
        var n = list.Count(b => b.SheetIndex == sheetIndex);
        var paired = list.Count(b => b.SheetIndex == sheetIndex && b.PairId is not null);
        return new BridgeClickResult(
            list,
            n == 0 ? "自动计算未找到可放位置" : $"自动布桥 {n} 个" + (paired > 0 ? $"（成对 {paired}）" : ""),
            true);
    }

    /// <summary>
    /// If a tab already faces a neighbor closer than 2× tool Ø, the neighbor
    /// must skip on the last pass too — otherwise its through-cut eats the web.
    /// </summary>
    public static IReadOnlyList<ProfileBridge> EnsureFacingPairs(
        IReadOnlyList<ProfileBridge> existing,
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>>? outlinesByPanel,
        double toolDiameterMm)
    {
        if (existing.Count == 0) return existing;
        var list = existing.ToList();
        var pairLimit = PairClearanceLimitMm(toolDiameterMm);
        var sheets = list.Select(b => b.SheetIndex).Distinct().ToList();
        foreach (var sheet in sheets)
            EnsureFacingPairsOnSheet(list, ops, outlinesByPanel, sheet, pairLimit);
        return list;
    }

    static void EnsureFacingPairsOnSheet(
        List<ProfileBridge> list,
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>>? outlinesByPanel,
        int sheetIndex,
        double pairLimit)
    {
        var contours = ContoursOnSheet(ops, sheetIndex)
            .Where(o => o.FeatureId is null)
            .ToList();
        if (contours.Count == 0) return;

        var outlines = new Dictionary<string, IReadOnlyList<Point2>>(StringComparer.Ordinal);
        foreach (var op in contours)
        {
            if (outlinesByPanel is not null
                && outlinesByPanel.TryGetValue(op.PanelId, out var ring)
                && ring.Count >= 2)
                outlines[op.PanelId] = ring;
            else if (op.Path is { Count: >= 2 } path)
                outlines[op.PanelId] = path.Select(p => new Point2(p.X, p.Y)).ToList();
        }

        var pending = list.Where(b => b.SheetIndex == sheetIndex && b.FeatureId is null).ToList();
        foreach (var b in pending)
        {
            if (b.PairId is not null && list.Any(x => x.Id == b.PairId))
                continue;

            var neighbor = FindForcedNeighbor(
                b.PanelId, new Point2(b.X, b.Y), outlines, pairLimit);
            if (neighbor is not { } nb)
                continue;
            if (outlines.TryGetValue(nb.PanelId, out var nbRing)
                && IsLargeBoard(BoundsOf(nbRing).W, BoundsOf(nbRing).H))
                continue;

            var pairOp = contours.FirstOrDefault(o =>
                o.PanelId == nb.PanelId && o.Path is { Count: >= 2 });
            if (pairOp?.Path is not { Count: >= 2 } pairPath)
                continue;

            var pairHit = PolylineQuery.Nearest(pairPath, nb.Point.X, nb.Point.Y, pairOp.ClosePath);
            if (pairHit is null)
                continue;

            var already = NearestSymbolOnPanel(
                list, pairOp.PanelId, pairOp.FeatureId, pairHit.Value.X, pairHit.Value.Y);
            if (already is not null && already.Dist <= DedupMm)
            {
                if (already.Bridge.PairId is not null && already.Bridge.PairId != b.Id)
                    continue;
                var idxSelf = list.FindIndex(x => x.Id == b.Id);
                var idxNb = list.FindIndex(x => x.Id == already.Bridge.Id);
                if (idxSelf >= 0)
                    list[idxSelf] = list[idxSelf] with { PairId = already.Bridge.Id };
                if (idxNb >= 0)
                    list[idxNb] = list[idxNb] with { PairId = b.Id };
                continue;
            }

            var id = NewId();
            var idx = list.FindIndex(x => x.Id == b.Id);
            if (idx >= 0)
                list[idx] = list[idx] with { PairId = id };
            list.Add(new ProfileBridge
            {
                Id = id,
                PanelId = pairOp.PanelId,
                FeatureId = pairOp.FeatureId,
                SheetIndex = sheetIndex,
                ArcLengthMm = pairHit.Value.ArcLengthMm,
                X = pairHit.Value.X,
                Y = pairHit.Value.Y,
                WidthMm = b.WidthMm,
                PairId = b.Id,
            });
        }
    }

    /// <summary>Auto-place on every sheet that has a profile contour.</summary>
    public static BridgeClickResult AutoPlaceAll(
        IReadOnlyList<ProfileBridge> existing,
        IReadOnlyList<CutOp> ops,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlinesByPanel,
        double toolDiameterMm,
        double widthMm,
        double tinyAreaM2 = TinyAreaM2,
        double largeAreaM2 = LargeAreaM2,
        double stripAspect = StripAspect)
    {
        var sheets = ops
            .Where(o =>
                o.Placed
                && o.Op == "contour"
                && o.FeatureId is null
                && o.Path is { Count: >= 2 })
            .Select(o => o.SheetIndex)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        if (sheets.Count == 0)
            return new BridgeClickResult(existing.ToList(), "请先计算刀路", false);

        IReadOnlyList<ProfileBridge> cur = existing;
        foreach (var s in sheets)
            cur = AutoPlace(cur, ops, outlinesByPanel, s, toolDiameterMm, widthMm,
                tinyAreaM2, largeAreaM2, stripAspect).Bridges;

        var n = cur.Count;
        var paired = cur.Count(b => b.PairId is not null);
        var panels = sheets.Sum(s =>
            ops.Count(o =>
                o.Placed && o.Op == "contour" && o.FeatureId is null
                && o.SheetIndex == s && o.Path is { Count: >= 2 }));
        return new BridgeClickResult(
            cur.ToList(),
            n == 0
                ? "自动计算未找到可放位置"
                : $"全部自动布桥 {n} 个 · {sheets.Count} 张大板 · {panels} 块"
                  + (paired > 0 ? $"（成对 {paired}）" : ""),
            true);
    }

    sealed record Box(double MinX, double MinY, double MaxX, double MaxY, double W, double H);

    sealed record PanelShape(
        string PanelId, CutOp Op, IReadOnlyList<Point2> Ring, Box Box,
        double LargeLimitM2, double StripAspectMin)
    {
        public double BoardAreaM2 => AreaM2(Box.W, Box.H);
        public bool IsStrip => IsLongStrip(Box.W, Box.H, StripAspectMin);
        public bool IsSmall => BoardAreaM2 <= LargeLimitM2;
        public bool IsLarge => BoardAreaM2 > LargeLimitM2 && !IsStrip;
    }

    static Box BoundsOf(IReadOnlyList<Point2> ring)
    {
        var minX = ring.Min(p => p.X);
        var minY = ring.Min(p => p.Y);
        var maxX = ring.Max(p => p.X);
        var maxY = ring.Max(p => p.Y);
        return new Box(minX, minY, maxX, maxY, maxX - minX, maxY - minY);
    }

    static void PlaceStrip(
        List<ProfileBridge> list,
        IReadOnlyList<CutOp> contours,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlines,
        PanelShape panel,
        int sheetIndex,
        double toolDiameterMm,
        double widthMm,
        double sameEdgeMergeMm,
        Func<string, bool> shouldPair)
    {
        var box = panel.Box;
        var vertical = box.H >= box.W;
        var longLen = vertical ? box.H : box.W;
        var inset = Math.Clamp(EndInsetMm, 20, Math.Max(20, longLen * 0.12));
        if (inset * 2 + 10 > longLen)
            inset = Math.Max(12, longLen * 0.2);

        var midX = (box.MinX + box.MaxX) * 0.5;
        var midY = (box.MinY + box.MaxY) * 0.5;

        void Add(Point2 near) =>
            TryAddAt(list, contours, outlines, sheetIndex, panel.PanelId,
                near, toolDiameterMm, widthMm, sameEdgeMergeMm, shouldPair);

        if (vertical)
        {
            Add(new Point2(midX, box.MaxY));
            Add(new Point2(box.MinX, box.MaxY - inset));
            Add(new Point2(box.MaxX, box.MaxY - inset));

            Add(new Point2(midX, box.MinY));
            Add(new Point2(box.MinX, box.MinY + inset));
            Add(new Point2(box.MaxX, box.MinY + inset));
            PlaceStripMiddles(Add, box, vertical: true, longLen, inset, midX, midY);
        }
        else
        {
            Add(new Point2(box.MaxX, midY));
            Add(new Point2(box.MaxX - inset, box.MinY));
            Add(new Point2(box.MaxX - inset, box.MaxY));

            Add(new Point2(box.MinX, midY));
            Add(new Point2(box.MinX + inset, box.MinY));
            Add(new Point2(box.MinX + inset, box.MaxY));
            PlaceStripMiddles(Add, box, vertical: false, longLen, inset, midX, midY);
        }
    }

    static void PlaceStripMiddles(
        Action<Point2> add, Box box, bool vertical, double longLen, double inset,
        double midX, double midY)
    {
        var shortSide = vertical ? box.W : box.H;
        if (shortSide >= StripOnePairMm)
        {
            if (vertical)
            {
                add(new Point2(box.MinX, midY));
                add(new Point2(box.MaxX, midY));
            }
            else
            {
                add(new Point2(midX, box.MinY));
                add(new Point2(midX, box.MaxY));
            }
            return;
        }

        if (shortSide > StripTwoPairMinMm)
        {
            var a = longLen / 3;
            var b = longLen * 2 / 3;
            if (vertical)
            {
                add(new Point2(box.MinX, box.MinY + a));
                add(new Point2(box.MaxX, box.MinY + a));
                add(new Point2(box.MinX, box.MinY + b));
                add(new Point2(box.MaxX, box.MinY + b));
            }
            else
            {
                add(new Point2(box.MinX + a, box.MinY));
                add(new Point2(box.MinX + a, box.MaxY));
                add(new Point2(box.MinX + b, box.MinY));
                add(new Point2(box.MinX + b, box.MaxY));
            }
            return;
        }

        if (vertical)
        {
            for (var y = box.MinY + inset + MiddleSpacingMm;
                 y <= box.MaxY - inset - 40;
                 y += MiddleSpacingMm)
            {
                add(new Point2(box.MinX, y));
                add(new Point2(box.MaxX, y));
            }
        }
        else
        {
            for (var x = box.MinX + inset + MiddleSpacingMm;
                 x <= box.MaxX - inset - 40;
                 x += MiddleSpacingMm)
            {
                add(new Point2(x, box.MinY));
                add(new Point2(x, box.MaxY));
            }
        }
    }

    static void PlaceSmall(
        List<ProfileBridge> list,
        IReadOnlyList<CutOp> contours,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlines,
        PanelShape panel,
        IReadOnlyList<PanelShape> all,
        int sheetIndex,
        double toolDiameterMm,
        double widthMm,
        double pairLimit,
        double tinyAreaM2,
        Func<string, bool> shouldPair)
    {
        var target = TargetSmallBridges(panel.BoardAreaM2, tinyAreaM2);
        var usedEdges = UsedEdgeIds(list, panel);
        var connected = ConnectedNeighborCount(list, panel.PanelId);
        var remaining = target - usedEdges.Count;
        if (remaining <= 0)
            return;
        if (connected >= 2)
            remaining = Math.Min(remaining, 1);

        foreach (var cand in RankSmallEdges(panel, all, pairLimit, usedEdges))
        {
            if (remaining <= 0) break;
            if (usedEdges.Contains(cand.Edge)) continue;
            if (!TryAddAt(list, contours, outlines, sheetIndex, panel.PanelId,
                    cand.Mid, toolDiameterMm, widthMm, 0, shouldPair))
                continue;
            remaining--;
            usedEdges.Add(cand.Edge);
        }
    }

    static HashSet<int> UsedEdgeIds(IReadOnlyList<ProfileBridge> list, PanelShape panel)
    {
        var edges = new HashSet<int>();
        foreach (var b in list.Where(b => b.PanelId == panel.PanelId))
            edges.Add(ClosestEdgeIndex(panel.Ring, new Point2(b.X, b.Y)));
        return edges;
    }

    static int ConnectedNeighborCount(IReadOnlyList<ProfileBridge> list, string panelId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in list.Where(b => b.PanelId == panelId && b.PairId is not null))
        {
            var pair = list.FirstOrDefault(x => x.Id == b.PairId);
            if (pair is not null && pair.PanelId != panelId)
                ids.Add(pair.PanelId);
        }
        return ids.Count;
    }

    static List<(int Edge, Point2 Mid, int Rank, double Len)> RankSmallEdges(
        PanelShape panel,
        IReadOnlyList<PanelShape> all,
        double pairLimit,
        HashSet<int> usedEdges)
    {
        var ring = panel.Ring;
        var n = ring.Count;
        var found = new List<(int Edge, Point2 Mid, int Rank, double Len)>();
        for (var i = 0; i < n; i++)
        {
            if (usedEdges.Contains(i)) continue;
            var a = ring[i];
            var b = ring[(i + 1) % n];
            var len = Dist(a, b);
            if (len < 25) continue;
            var mid = Lerp(a, b, 0.5);
            var rank = 2;
            var bestD = double.PositiveInfinity;
            foreach (var other in all)
            {
                if (other.PanelId == panel.PanelId) continue;
                var (q, d) = PolygonDistance.ClosestPoint(other.Ring, mid);
                if (double.IsNaN(d) || d >= pairLimit || d >= bestD) continue;
                var outward = PolygonDistance.OutwardUnitNormal(panel.Ring, mid);
                var vx = q.X - mid.X;
                var vy = q.Y - mid.Y;
                if (d > 1e-6 && vx * outward.X + vy * outward.Y <= 0)
                    continue;
                bestD = d;
                rank = other.IsLarge ? 1 : 0;
            }

            found.Add((i, mid, rank, len));
        }

        return found
            .OrderBy(c => c.Rank)
            .ThenByDescending(c => c.Len)
            .ToList();
    }

    static int ClosestEdgeIndex(IReadOnlyList<Point2> ring, Point2 p)
    {
        var best = 0;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var q = ClosestOnSegment(p, a, b);
            var d = Dist(p, q);
            if (d >= bestD) continue;
            bestD = d;
            best = i;
        }
        return best;
    }

    static Point2 ClosestOnSegment(Point2 p, Point2 a, Point2 b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var len2 = vx * vx + vy * vy;
        if (len2 < 1e-18) return a;
        var t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return new Point2(a.X + t * vx, a.Y + t * vy);
    }

    /// <returns>True if a new bridge was added on <paramref name="panelId"/>.</returns>
    public static bool TryAddAt(
        List<ProfileBridge> list,
        IReadOnlyList<CutOp> contours,
        IReadOnlyDictionary<string, IReadOnlyList<Point2>> outlines,
        int sheetIndex,
        string panelId,
        Point2 near,
        double toolDiameterMm,
        double widthMm,
        double sameEdgeMergeMm = 0,
        Func<string, bool>? shouldPairWith = null)
    {
        var op = contours.FirstOrDefault(o =>
            o.PanelId == panelId && o.FeatureId is null && o.Path is { Count: >= 2 });
        if (op?.Path is not { Count: >= 2 } path)
            return false;
        var hit = PolylineQuery.Nearest(path, near.X, near.Y, op.ClosePath);
        if (hit is null)
            return false;

        var hitPt = new Point2(hit.Value.X, hit.Value.Y);
        if (outlines.TryGetValue(panelId, out var ring) && ring.Count >= 2)
        {
            var corner = ring.Min(p => Dist(p, hitPt));
            if (corner < CornerSkipMm)
                return false;
        }

        if (CountOnPanel(list, panelId) >= MaxPerPanel)
            return false;
        var existing = NearestSymbolOnPanel(list, panelId, op.FeatureId, hit.Value.X, hit.Value.Y);
        if (existing is not null && existing.Dist <= DedupMm)
            return false;

        if (sameEdgeMergeMm > DedupMm
            && outlines.TryGetValue(panelId, out var mergeRing)
            && mergeRing.Count >= 2
            && SameEdgeTooClose(list, panelId, mergeRing, hitPt, sameEdgeMergeMm))
            return false;

        var width = widthMm > 0.05 ? widthMm : DefaultWidthMm;
        var neighbor = FindForcedNeighbor(
            panelId, hitPt, outlines,
            PairClearanceLimitMm(toolDiameterMm));

        var pairOk = neighbor is { } nb0
                     && (shouldPairWith is null || shouldPairWith(nb0.PanelId));

        if (pairOk && neighbor is { } nb)
        {
            var pairOp = contours.FirstOrDefault(o =>
                o.PanelId == nb.PanelId && o.FeatureId is null && o.Path is { Count: >= 2 });
            if (pairOp?.Path is { Count: >= 2 } pairPath)
            {
                var pairHit = PolylineQuery.Nearest(pairPath, nb.Point.X, nb.Point.Y, pairOp.ClosePath);
                if (pairHit is not null)
                {
                    var already = NearestSymbolOnPanel(
                        list, pairOp.PanelId, pairOp.FeatureId, pairHit.Value.X, pairHit.Value.Y);
                    if (already is not null && already.Dist <= DedupMm)
                    {
                        if (already.Bridge.PairId is not null)
                            return false;
                        var idA = NewId();
                        list.Add(new ProfileBridge
                        {
                            Id = idA,
                            PanelId = panelId,
                            FeatureId = op.FeatureId,
                            SheetIndex = sheetIndex,
                            ArcLengthMm = hit.Value.ArcLengthMm,
                            X = hit.Value.X,
                            Y = hit.Value.Y,
                            WidthMm = width,
                            PairId = already.Bridge.Id,
                        });
                        var idx = list.FindIndex(b => b.Id == already.Bridge.Id);
                        if (idx >= 0)
                            list[idx] = list[idx] with { PairId = idA };
                        return true;
                    }

                    if (CountOnPanel(list, nb.PanelId) >= MaxPerPanel)
                        return false;

                    var id1 = NewId();
                    var id2 = NewId();
                    list.Add(new ProfileBridge
                    {
                        Id = id1,
                        PanelId = panelId,
                        FeatureId = op.FeatureId,
                        SheetIndex = sheetIndex,
                        ArcLengthMm = hit.Value.ArcLengthMm,
                        X = hit.Value.X,
                        Y = hit.Value.Y,
                        WidthMm = width,
                        PairId = id2,
                    });
                    list.Add(new ProfileBridge
                    {
                        Id = id2,
                        PanelId = pairOp.PanelId,
                        FeatureId = pairOp.FeatureId,
                        SheetIndex = sheetIndex,
                        ArcLengthMm = pairHit.Value.ArcLengthMm,
                        X = pairHit.Value.X,
                        Y = pairHit.Value.Y,
                        WidthMm = width,
                        PairId = id1,
                    });
                    return true;
                }
            }
        }

        list.Add(new ProfileBridge
        {
            Id = NewId(),
            PanelId = panelId,
            FeatureId = op.FeatureId,
            SheetIndex = sheetIndex,
            ArcLengthMm = hit.Value.ArcLengthMm,
            X = hit.Value.X,
            Y = hit.Value.Y,
            WidthMm = width,
            PairId = null,
        });
        return true;
    }

    static bool SameEdgeTooClose(
        IReadOnlyList<ProfileBridge> list,
        string panelId,
        IReadOnlyList<Point2> ring,
        Point2 hit,
        double mergeMm)
    {
        var edge = ClosestEdgeIndex(ring, hit);
        foreach (var b in list.Where(b => b.PanelId == panelId))
        {
            var other = new Point2(b.X, b.Y);
            if (ClosestEdgeIndex(ring, other) != edge)
                continue;
            if (Dist(hit, other) < mergeMm)
                return true;
        }
        return false;
    }

    static Point2 Lerp(Point2 a, Point2 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    static double Dist(Point2 a, Point2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static string NewId() => Guid.NewGuid().ToString("N")[..10];
}
