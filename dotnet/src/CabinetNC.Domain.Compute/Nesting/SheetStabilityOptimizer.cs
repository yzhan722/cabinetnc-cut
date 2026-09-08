namespace CabinetNC.Domain.Nesting;

using CabinetNC.Domain.Parts;

/// <summary>
/// Post-nest pass on one sheet. Step 1 is always: get dangerously narrow
/// vertical strips off the sheet width-edges. Any wider portrait board
/// (150mm, 240mm, 400mm, a side panel, …) may take that edge.
/// Small / landscape parts are only nudged afterwards if the strip move
/// left a true overlap — remnant cosmetics are out of scope.
/// A later pass centers 套裁 (parts-in-part) children in their host voids,
/// even when the sheet has no edge strips.
/// </summary>
public static class SheetStabilityOptimizer
{
    public const double StripAspectMin = 3.5;
    /// <summary>Narrower than this (and standing) is a dangerous 竖条.</summary>
    public const double StripMaxShortMm = 100;
    /// <summary>Portrait columns at least this wide may occupy the width-edge.</summary>
    public const double StableColumnMinMm = 140;
    public const double EdgeSlackMm = 80;

    public enum Kind { Large, Small, Strip }

    public sealed class Result
    {
        public required IReadOnlyList<NestPlacement> Placements { get; init; }
        public int MovedCount { get; init; }
        public int StripMoved { get; init; }
        public int LargeMoved { get; init; }
        public int PipMoved { get; init; }
        public bool Improved { get; init; }
        public required string Message { get; init; }
        /// <summary>Step-by-step why the pass moved or refused, for usage logs.</summary>
        public IReadOnlyList<string> Reasons { get; init; } = [];
        public int StripCount { get; init; }
        public int ColumnCount { get; init; }
        public double StartScore { get; init; }
        public double EndScore { get; init; }
    }

    public static Kind Classify(double w, double h, double medianArea, double medianPortraitShort = 0)
    {
        // Only standing bars are dangerous on the sheet width-edge.
        // A 589×65 rail is long, but it is horizontal — not a 竖条.
        if (h >= w * StripAspectMin)
        {
            if (w <= StripMaxShortMm)
                return Kind.Strip;
            if (medianPortraitShort > StripMaxShortMm && w <= medianPortraitShort * 0.70)
                return Kind.Strip;
        }
        return w * h >= Math.Max(medianArea, 1) ? Kind.Large : Kind.Small;
    }

    public static Result Optimize(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        int sheetIndex,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm,
        IReadOnlySet<string>? locked = null,
        IReadOnlySet<string>? frozen = null,
        IReadOnlyList<PartInPartSlot>? partInPartSlots = null)
    {
        var reasons = new List<string>();
        var byPanel = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        var lockedSet = locked ?? new HashSet<string>(StringComparer.Ordinal);
        var frozenSet = frozen ?? new HashSet<string>(StringComparer.Ordinal);
        var work = placements.Select(Clone).ToList();
        var sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
        reasons.Add($"sheet={sheetIndex} parts={sheet.Count} W={sheetW:0} H={sheetH:0} border={borderMm:0} gap={spacingMm:0} locked={lockedSet.Count} frozen={frozenSet.Count} pip={partInPartSlots?.Count ?? 0}");
        if (sheet.Count < 2)
            return Fail(work, "本张板件不足，无需优化", reasons);

        var sizes = new Dictionary<string, (double W, double H)>(StringComparer.Ordinal);
        var areas = new List<double>();
        var portraitShorts = new List<double>();
        foreach (var place in sheet)
        {
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) continue;
            var (w, h) = NestDrag.SizeRotated(panel, place.RotationDeg);
            sizes[place.PanelId] = (w, h);
            areas.Add(w * h);
            var shortSide = Math.Min(w, h);
            var longSide = Math.Max(w, h);
            if (longSide >= shortSide * 2)
                portraitShorts.Add(shortSide);
        }
        if (sizes.Count < 2)
            return Fail(work, "本张板件不足，无需优化", reasons);

        areas.Sort();
        var median = areas[areas.Count / 2];
        portraitShorts.Sort();
        var medianPortrait = portraitShorts.Count > 0 ? portraitShorts[portraitShorts.Count / 2] : 0;
        reasons.Add($"medianArea={median:0} medianPortraitShort={medianPortrait:0} portraitCount={portraitShorts.Count}");

        var kinds = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var (id, sz) in sizes)
            kinds[id] = Classify(sz.W, sz.H, median, medianPortrait);

        bool CanMove(string id) =>
            !lockedSet.Contains(id) && !frozenSet.Contains(id);

        LogEdgeSnapshot(reasons, sheet, byPanel, sizes, kinds, sheetW, CanMove);
        reasons.Add("rule:strips-off-edge-first");

        var strips = sheet
            .Where(p => kinds.TryGetValue(p.PanelId, out var k) && k == Kind.Strip)
            .ToList();
        reasons.Add($"strips={strips.Count} [{string.Join(",", strips.Select(p => p.PanelId))}]");

        var startScore = Score(sheet, sizes, kinds, sheetW, borderMm);
        var beforePos = work.ToDictionary(p => p.PanelId, p => (p.OffsetX, p.OffsetY), StringComparer.Ordinal);
        reasons.Add($"startScore={startScore:0.000}");

        var colCount = 0;
        var slid = 0;
        var stripImproved = false;
        if (strips.Count > 0)
        {
            colCount = TryColumnSwap(
                work, sheetIndex, byPanel, sizes, kinds, CanMove,
                sheetW, sheetH, borderMm, spacingMm, reasons);

            sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
            foreach (var id in strips.Select(p => p.PanelId).Where(CanMove)
                         .OrderBy(id => CenterNorm(sheet, sizes, id, sheetW)))
            {
                if (SlideToward(work, sheetIndex, id, byPanel, sizes, kinds, sheetW, sheetH, borderMm, spacingMm))
                {
                    slid++;
                    reasons.Add($"slide:{id}");
                }
                else
                    reasons.Add($"slide-blocked:{id}");
            }

            sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
            var stripEndScore = Score(sheet, sizes, kinds, sheetW, borderMm);
            var startEdge = CountEdgeStrips(placements.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
            var endEdge = CountEdgeStrips(sheet, sizes, kinds, sheetW, borderMm);
            stripImproved = endEdge < startEdge && stripEndScore < startScore - 1e-6;
            reasons.Add($"stripScore={stripEndScore:0.000} edgeStrips {startEdge}->{endEdge} stripImproved={stripImproved}");

            if (!stripImproved)
            {
                var why = SummarizeNoMove(reasons, strips.Count, slid, colCount, 0, startScore, stripEndScore);
                reasons.Add($"strip-rollback:{why}");
                work = placements.Select(Clone).ToList();
            }
        }

        var pipMoved = 0;
        if (partInPartSlots is { Count: > 0 })
        {
            reasons.Add("rule:pip-center-in-void");
            pipMoved = PartsInPartPacker.CenterInVoids(
                work, byPanel, partInPartSlots, sheetIndex, spacingMm, lockedSet, reasons);
        }

        var stripMoved = 0;
        var largeMoved = 0;
        var movedCount = 0;
        foreach (var p in work)
        {
            if (!beforePos.TryGetValue(p.PanelId, out var orig)) continue;
            if (Math.Abs(p.OffsetX - orig.OffsetX) < 0.2 && Math.Abs(p.OffsetY - orig.OffsetY) < 0.2)
                continue;
            movedCount++;
            if (kinds.TryGetValue(p.PanelId, out var k) && k == Kind.Strip) stripMoved++;
            else largeMoved++;
        }

        sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
        var endScore = Score(sheet, sizes, kinds, sheetW, borderMm);
        var improved = stripImproved || pipMoved > 0;
        reasons.Add($"endScore={endScore:0.000} moved={movedCount} stripMoved={stripMoved} largeMoved={largeMoved} pipMoved={pipMoved} improved={improved}");

        if (!improved)
        {
            var why = strips.Count == 0
                ? "未识别到危险窄条（短边不够窄，或相对同列竖板不够细），未移动"
                : SummarizeNoMove(reasons, strips.Count, slid, colCount, movedCount, startScore, endScore);
            reasons.Add($"result:{why}");
            return new Result
            {
                Placements = placements.Select(Clone).ToList(),
                Message = why,
                Reasons = reasons,
                StripCount = strips.Count,
                ColumnCount = colCount,
                StartScore = startScore,
                EndScore = endScore,
            };
        }

        var msg = stripImproved && pipMoved > 0
            ? $"密排优化 · 窄条进中 {stripMoved} · 套裁居中 {pipMoved} · 共移动 {movedCount} 件"
            : stripImproved
                ? $"密排优化 · 窄条进中 {stripMoved} · 共移动 {movedCount} 件"
                : $"密排优化 · 套裁居中 {pipMoved} 件";
        reasons.Add($"result:{msg}");
        return new Result
        {
            Placements = work,
            MovedCount = movedCount,
            StripMoved = stripMoved,
            LargeMoved = largeMoved,
            PipMoved = pipMoved,
            Improved = true,
            Message = msg,
            Reasons = reasons,
            StripCount = strips.Count,
            ColumnCount = colCount,
            StartScore = startScore,
            EndScore = endScore,
        };
    }

    static Result Fail(
        List<NestPlacement> work,
        string message,
        List<string> reasons,
        int stripCount = 0,
        int columnCount = 0)
    {
        reasons.Add($"result:{message}");
        return new Result
        {
            Placements = work,
            Message = message,
            Reasons = reasons,
            StripCount = stripCount,
            ColumnCount = columnCount,
        };
    }

    static string SummarizeNoMove(
        List<string> reasons,
        int stripCount,
        int slid,
        int colCount,
        int movedCount,
        double startScore,
        double endScore)
    {
        if (reasons.Any(r => r.StartsWith("insert-reject:overlap", StringComparison.Ordinal)
                             || r.StartsWith("swap-reject:overlap", StringComparison.Ordinal)
                             || r.StartsWith("pair-reject:overlap", StringComparison.Ordinal)))
            return "换列后板件重叠，方案被拒绝，未移动";
        if (reasons.Any(r => r.StartsWith("insert-reject:polygon", StringComparison.Ordinal)
                             || r.StartsWith("swap-reject:polygon", StringComparison.Ordinal)
                             || r.StartsWith("pair-reject:polygon", StringComparison.Ordinal)))
            return "换列后轮廓间距不够，方案被拒绝，未移动";
        if (reasons.Any(r => r.StartsWith("insert-reject:bounds", StringComparison.Ordinal)
                             || r.StartsWith("swap-reject:bounds", StringComparison.Ordinal)
                             || r.StartsWith("pair-reject:bounds", StringComparison.Ordinal)))
            return "换列后超出板边，方案被拒绝，未移动";
        if (reasons.Any(r => r.StartsWith("insert-reject:score", StringComparison.Ordinal)
                             || r.StartsWith("swap-reject:score", StringComparison.Ordinal)
                             || r.StartsWith("pair-reject:score", StringComparison.Ordinal)))
            return "换列后贴边分未改善，未移动";
        if (reasons.Any(r => r.Contains("cols=0", StringComparison.Ordinal) || r.Contains("cols=1", StringComparison.Ordinal)))
            return "竖板无法分成多列（凹凸外框可能并成一列），未移动";
        if (reasons.Any(r => r.StartsWith("skip:not-on-edge", StringComparison.Ordinal)))
            return "窄条已不在左右板边，未移动";
        if (reasons.Any(r => r.StartsWith("skip:not-extreme", StringComparison.Ordinal)))
            return "窄条不在左右最外列，未移动";
        if (reasons.Any(r => r.StartsWith("skip:no-slot", StringComparison.Ordinal)
                             || r.StartsWith("skip:no-partner", StringComparison.Ordinal)
                             || r.StartsWith("skip:new-edge-not-stable", StringComparison.Ordinal)))
            return "没有可插入的中间列位，未移动";
        if (reasons.Any(r => r.StartsWith("skip:locked-col", StringComparison.Ordinal)))
            return "窄条所在列已锁定或套裁，未移动";
        if (stripCount > 0 && slid == 0 && colCount < 2)
            return "窄条无法平移且未能分列换位，未移动";
        if (movedCount > 0 && endScore >= startScore - 1e-6)
            return "有位移但贴边分未下降，已回退";
        return "当前摆位已贴边/居中，无需调整";
    }

    static void LogEdgeSnapshot(
        List<string> reasons,
        List<NestPlacement> sheet,
        IReadOnlyDictionary<string, Panel> byPanel,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        double sheetW,
        Func<string, bool> canMove)
    {
        var rows = new List<(string Id, double MinX, double MaxX, double W, double H, Kind Kind, bool Movable)>();
        foreach (var place in sheet)
        {
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            sizes.TryGetValue(place.PanelId, out var sz);
            kinds.TryGetValue(place.PanelId, out var kind);
            rows.Add((place.PanelId, box.MinX, box.MaxX, sz.W, sz.H, kind, canMove(place.PanelId)));
        }
        if (rows.Count == 0) return;
        var right = rows.OrderByDescending(r => r.MaxX).Take(4).ToList();
        var left = rows.OrderBy(r => r.MinX).Take(3).ToList();
        reasons.Add("rightmost=" + string.Join(" | ", right.Select(r =>
            $"{r.Id} kind={r.Kind} aabbW={r.MaxX - r.MinX:0} placed={r.W:0}x{r.H:0} maxX={r.MaxX:0} edgeGap={sheetW - r.MaxX:0} move={r.Movable}")));
        reasons.Add("leftmost=" + string.Join(" | ", left.Select(r =>
            $"{r.Id} kind={r.Kind} aabbW={r.MaxX - r.MinX:0} minX={r.MinX:0} move={r.Movable}")));
    }

    static bool SlideToward(
        List<NestPlacement> work,
        int sheetIndex,
        string id,
        IReadOnlyDictionary<string, Panel> byPanel,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm)
    {
        if (!byPanel.TryGetValue(id, out var panel)) return false;
        var moved = false;
        double[] steps = [40, 20, 10, 5, 2];
        foreach (var step in steps)
        {
            for (var n = 0; n < 80; n++)
            {
                var idx = work.FindIndex(p => p.PanelId == id && p.SheetIndex == sheetIndex);
                if (idx < 0) return moved;
                var cur = work[idx];
                var (w, _) = sizes[id];
                var minX = cur.OffsetX;
                var maxX = cur.OffsetX + w;
                if (minX > borderMm + EdgeSlackMm && maxX < sheetW - borderMm - EdgeSlackMm)
                    return moved;
                var cx = cur.OffsetX + w * 0.5;
                var mid = sheetW * 0.5;
                var delta = mid - cx;
                if (Math.Abs(delta) < step * 0.5) break;
                var dir = Math.Sign(delta);
                var nextCx = cx + dir * step;
                if (dir > 0) nextCx = Math.Min(nextCx, mid);
                else nextCx = Math.Max(nextCx, mid);
                var ox = nextCx - w * 0.5;
                var (clampedOx, clampedOy) = NestDrag.ClampOnSheet(
                    panel, ox, cur.OffsetY, cur.RotationDeg, sheetW, sheetH, borderMm);
                var candidate = With(cur, clampedOx, clampedOy);
                if (Math.Abs(candidate.OffsetX - cur.OffsetX) < 0.2) break;

                var trial = Replace(work, idx, candidate);
                if (InvalidWhy(
                        trial, work, sheetIndex, byPanel, sheetW, sheetH, borderMm, spacingMm) is not null)
                    break;
                var trialSheet = trial.Where(p => p.SheetIndex == sheetIndex).ToList();
                var selfHits = NestValidator.FindPolygonCollisions(byPanel.Values.ToList(), trialSheet, 0);
                if (selfHits.Any(h => h.PanelIdA == id || h.PanelIdB == id))
                    break;
                var before = Score(work.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
                var after = Score(trial.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
                if (after >= before - 1e-6) break;
                work.Clear();
                work.AddRange(trial);
                moved = true;
            }
        }
        return moved;
    }

    static int TryColumnSwap(
        List<NestPlacement> work,
        int sheetIndex,
        IReadOnlyDictionary<string, Panel> byPanel,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        Func<string, bool> canMove,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm,
        List<string> reasons)
    {
        var sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
        var skinnies = sheet
            .Where(p => kinds.TryGetValue(p.PanelId, out var k) && k == Kind.Strip && canMove(p.PanelId))
            .ToList();
        if (skinnies.Count == 0)
        {
            reasons.Add("swap:no-movable-strip");
            return 0;
        }

        double bandMinY = double.MaxValue, bandMaxY = double.MinValue;
        foreach (var skinny in skinnies)
        {
            if (!byPanel.TryGetValue(skinny.PanelId, out var sp)) continue;
            var box = NestDrag.Aabb(sp, skinny.OffsetX, skinny.OffsetY, skinny.RotationDeg);
            bandMinY = Math.Min(bandMinY, box.MinY);
            bandMaxY = Math.Max(bandMaxY, box.MaxY);
        }
        if (bandMaxY <= bandMinY)
        {
            reasons.Add("swap:empty-band");
            return 0;
        }

        var lastColCount = 0;
        foreach (var fromRight in new[] { true, false })
        {
            sheet = work.Where(p => p.SheetIndex == sheetIndex).ToList();
            var cols = MergePackedColumns(
                BuildColumns(sheet, byPanel, bandMinY, bandMaxY, spacingMm),
                spacingMm);
            AddWidePortraitColumns(cols, sheet, byPanel);
            var portraitIds = cols.SelectMany(c => c.Ids).ToHashSet(StringComparer.Ordinal);
            AttachSatellites(cols, sheet, byPanel, reasons);
            lastColCount = Math.Max(lastColCount, cols.Count);
            var colDump = string.Join(",", cols.Select((c, i) =>
                $"[{i}]n={c.Ids.Count} w={c.Width:0} cx={c.Cx:0} ids={string.Join("+", c.Ids)}"));
            reasons.Add($"swap:{(fromRight ? "right" : "left")} bandY={bandMinY:0}..{bandMaxY:0} cols={cols.Count} {colDump}");

            if (cols.Count < 2)
            {
                reasons.Add($"skip:few-cols:{(fromRight ? "right" : "left")}");
                continue;
            }

            var run = FindSkinnyEdgeRun(cols, fromRight, borderMm, sheetW, spacingMm);
            if (run is null)
            {
                reasons.Add($"skip:no-edge-run:{(fromRight ? "right" : "left")}");
                continue;
            }
            var (runStart, runEnd) = run.Value;
            if (Enumerable.Range(runStart, runEnd - runStart + 1).Any(i => cols[i].Ids.Any(id => !canMove(id))))
            {
                reasons.Add($"skip:locked-run:{runStart}..{runEnd}");
                continue;
            }

            var runMin = cols[runStart].MinX;
            var runMax = cols[runEnd].MaxX;
            reasons.Add($"run:{(fromRight ? "right" : "left")} {runStart}..{runEnd} x={runMin:0}..{runMax:0} ids={string.Join("+", Enumerable.Range(runStart, runEnd - runStart + 1).SelectMany(i => cols[i].Ids))}");

            var midIdx = cols.Count / 2;
            var edgeIdx = FindStableInterior(cols, fromRight ? runStart - 1 : runEnd + 1, fromRight ? -1 : 1);
            var lo = fromRight ? 0 : runEnd + 1;
            var hi = fromRight ? runStart - 1 : cols.Count - 1;
            var widestW = 0.0;
            for (var i = lo; i <= hi; i++)
            {
                if (cols[i].Width + 1e-6 < StableColumnMinMm) continue;
                if (cols[i].Ids.Any(id => !canMove(id))) continue;
                widestW = Math.Max(widestW, cols[i].Width);
            }
            var preferLargeSwap = widestW >= 200;

            var placed = false;
            if (preferLargeSwap)
            {
                placed = TryPairSwap(
                    work, cols, runStart, runEnd, fromRight, canMove,
                    sheetIndex, byPanel, portraitIds, sizes, kinds,
                    sheetW, sheetH, borderMm, spacingMm, reasons);
            }

            if (!placed && (edgeIdx < 0 || cols[edgeIdx].Width + 1e-6 < StableColumnMinMm))
            {
                reasons.Add($"skip:new-edge-not-stable:{(fromRight ? "right" : "left")} run={runStart}..{runEnd}");
            }
            else if (!placed)
            {
                var slots = new List<int>();
                var runW = runMax - runMin;
                for (var i = lo; i <= hi; i++)
                {
                    if (cols[i].Ids.Any(id => !canMove(id))) continue;
                    if (cols[i].Width + 1e-6 < StableColumnMinMm) continue;
                    if (i != edgeIdx && OverlapsX(cols[i], cols[edgeIdx])) continue;
                    var newMin = cols[i].MinX;
                    var newMax = newMin + runW;
                    if (newMin <= borderMm + EdgeSlackMm) continue;
                    if (newMax >= sheetW - borderMm - EdgeSlackMm) continue;
                    if (!InsertFitsOnSheet(cols, runStart, runEnd, i, edgeIdx, sheetW, borderMm, spacingMm))
                    {
                        reasons.Add($"skip:slot-overflow->{i}");
                        continue;
                    }
                    slots.Add(i);
                }
                slots = slots
                    .OrderBy(i => Math.Abs(i - midIdx))
                    .ThenBy(i => Math.Abs(i - (fromRight ? runStart : runEnd)))
                    .ToList();
                if (slots.Count == 0)
                    reasons.Add($"skip:no-slot:{(fromRight ? "right" : "left")} run={runStart}..{runEnd}");

                foreach (var slot in slots)
                {
                    var spanLo = Math.Min(runStart, slot);
                    var spanHi = Math.Max(runEnd, slot);
                    if (Enumerable.Range(spanLo, spanHi - spanLo + 1).Any(i => cols[i].Ids.Any(id => !canMove(id))))
                    {
                        reasons.Add($"skip:slot-locked->{slot}");
                        continue;
                    }

                    var trial = work.Select(Clone).ToList();
                    InsertRun(trial, cols, runStart, runEnd, slot, edgeIdx, spacingMm);
                    UnstickSmalls(
                        trial, sheetIndex, byPanel, portraitIds, canMove,
                        sheetW, sheetH, borderMm, spacingMm, reasons);
                    var why = InvalidWhy(
                        trial, work, sheetIndex, byPanel, sheetW, sheetH, borderMm, spacingMm,
                        (a, b) => portraitIds.Contains(a) && portraitIds.Contains(b));
                    if (why is not null)
                    {
                        reasons.Add($"insert-reject:{why}:run {runStart}..{runEnd}->[{slot}] {string.Join("+", cols[slot].Ids)}");
                        continue;
                    }
                    var before = Score(work.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
                    var after = Score(trial.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
                    if (after >= before - 1e-6)
                    {
                        reasons.Add($"insert-reject:score:run {runStart}..{runEnd}->[{slot}] {before:0.000}->{after:0.000}");
                        continue;
                    }
                    reasons.Add($"insert-ok:run {runStart}..{runEnd}->[{slot}] score {before:0.000}->{after:0.000}");
                    work.Clear();
                    work.AddRange(trial);
                    placed = true;
                    break;
                }
            }

            if (!placed && !preferLargeSwap)
            {
                TryPairSwap(
                    work, cols, runStart, runEnd, fromRight, canMove,
                    sheetIndex, byPanel, portraitIds, sizes, kinds,
                    sheetW, sheetH, borderMm, spacingMm, reasons);
            }
        }
        return lastColCount;
    }

    static (int Start, int End)? FindSkinnyEdgeRun(
        List<Column> cols,
        bool fromRight,
        double borderMm,
        double sheetW,
        double spacingMm)
    {
        bool Adjacent(Column a, Column b) =>
            b.MinX - a.MaxX <= spacingMm + 40;

        if (fromRight)
        {
            if (cols[^1].MaxX < sheetW - borderMm - EdgeSlackMm) return null;
            if (!IsSkinnyColumn(cols[^1])) return null;
            var start = cols.Count - 1;
            while (start > 0 && IsSkinnyColumn(cols[start - 1]) && Adjacent(cols[start - 1], cols[start]))
            {
                if (OverlapsStableInterior(cols, start - 1, -1))
                    break;
                start--;
            }
            return (start, cols.Count - 1);
        }

        if (cols[0].MinX > borderMm + EdgeSlackMm) return null;
        if (!IsSkinnyColumn(cols[0])) return null;
        var end = 0;
        while (end < cols.Count - 1 && IsSkinnyColumn(cols[end + 1]) && Adjacent(cols[end], cols[end + 1]))
        {
            if (OverlapsStableInterior(cols, end + 1, 1))
                break;
            end++;
        }
        return (0, end);
    }

    static int FindStableInterior(List<Column> cols, int from, int dir)
    {
        for (var i = from; i >= 0 && i < cols.Count; i += dir)
        {
            if (!IsSkinnyColumn(cols[i]))
                return i;
        }
        return -1;
    }

    static bool OverlapsStableInterior(List<Column> cols, int candidateIdx, int dir)
    {
        for (var i = candidateIdx + dir; i >= 0 && i < cols.Count; i += dir)
        {
            if (IsSkinnyColumn(cols[i])) continue;
            return OverlapsX(cols[candidateIdx], cols[i]);
        }
        return false;
    }

    static bool OverlapsX(Column a, Column b) =>
        Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX) > 1;

    /// <summary>
    /// Shifting the columns between the run and the slot must not push the new
    /// edge occupant off the sheet. Overlapping-X columns make runPitch larger
    /// than the actual vacated edge strip.
    /// </summary>
    static bool InsertFitsOnSheet(
        List<Column> cols,
        int runStart,
        int runEnd,
        int slotIdx,
        int edgeIdx,
        double sheetW,
        double borderMm,
        double spacingMm)
    {
        var runMin = cols[runStart].MinX;
        var runPitch = cols[runEnd].MaxX - runMin + spacingMm;
        var lo = borderMm - 0.05;
        var hi = sheetW - borderMm + 0.05;
        bool Ok(double minX, double maxX) => minX >= lo && maxX <= hi;

        if (runStart > slotIdx)
        {
            var dx = cols[slotIdx].MinX - runMin;
            if (!Ok(cols[runStart].MinX + dx, cols[runEnd].MaxX + dx))
                return false;
            for (var i = slotIdx; i < runStart; i++)
            {
                if (i != edgeIdx && OverlapsX(cols[i], cols[edgeIdx]))
                    continue;
                if (!Ok(cols[i].MinX + runPitch, cols[i].MaxX + runPitch))
                    return false;
            }
        }
        else
        {
            var dx = cols[slotIdx].MinX - runMin;
            if (!Ok(cols[runStart].MinX + dx, cols[runEnd].MaxX + dx))
                return false;
            for (var i = runEnd + 1; i <= slotIdx; i++)
            {
                if (i != edgeIdx && OverlapsX(cols[i], cols[edgeIdx]))
                    continue;
                if (!Ok(cols[i].MinX - runPitch, cols[i].MaxX - runPitch))
                    return false;
            }
        }
        return true;
    }

    static bool IsSkinnyColumn(Column col) =>
        col.Width + 1e-6 < StableColumnMinMm;

    sealed class Column
    {
        public List<string> Ids { get; } = [];
        public List<double> Centers { get; } = [];
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double Width => Math.Max(0, MaxX - MinX);
        public double Cx => Centers.Count == 0 ? (MinX + MaxX) * 0.5 : Centers.Average();
    }

    static List<Column> BuildColumns(
        List<NestPlacement> sheet,
        IReadOnlyDictionary<string, Panel> byPanel,
        double bandMinY,
        double bandMaxY,
        double spacingMm)
    {
        var items = new List<(NestPlacement Place, double MinX, double MaxX, double Cx, double W, double H)>();
        foreach (var place in sheet)
        {
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            if (box.MaxY < bandMinY + 1 || box.MinY > bandMaxY - 1) continue;
            var w = box.MaxX - box.MinX;
            var h = box.MaxY - box.MinY;
            if (h < w * 1.15 && w > StripMaxShortMm) continue;
            items.Add((place, box.MinX, box.MaxX, (box.MinX + box.MaxX) * 0.5, w, h));
        }
        items.Sort((a, b) => a.Cx.CompareTo(b.Cx));

        var cols = new List<Column>();
        foreach (var it in items)
        {
            var hit = cols.Count == 0 ? null : cols[^1];
            var gap = hit is null ? double.PositiveInfinity : it.Cx - hit.Cx;
            var thresh = Math.Max(spacingMm + 20, Math.Max(hit?.Width ?? 0, it.W) * 0.45);
            var itSkinny = IsSkinnyWidth(it.W, it.H);
            var hitSkinny = hit is not null && IsSkinnyColumn(hit);
            if (hit is not null && itSkinny != hitSkinny)
                thresh = Math.Min(thresh, spacingMm + 8);
            if (hit is null || gap > thresh)
            {
                cols.Add(new Column
                {
                    MinX = it.MinX,
                    MaxX = it.MaxX,
                    Ids = { it.Place.PanelId },
                    Centers = { it.Cx },
                });
            }
            else
            {
                hit.Ids.Add(it.Place.PanelId);
                hit.Centers.Add(it.Cx);
                hit.MinX = Math.Min(hit.MinX, it.MinX);
                hit.MaxX = Math.Max(hit.MaxX, it.MaxX);
            }
        }
        return cols;
    }

    /// <summary>
    /// Adjacent portrait columns that only overlap by a kerf/notch (&lt;= spacing+8)
    /// are one packed stack. Larger X-overlap is a Y-stack (leave separate so a
    /// mid-height strip is not dragged onto the sheet edge).
    /// </summary>
    static List<Column> MergePackedColumns(List<Column> cols, double spacingMm)
    {
        if (cols.Count < 2) return cols;
        var pack = spacingMm + 1;
        var merged = new List<Column> { cols[0] };
        for (var i = 1; i < cols.Count; i++)
        {
            var prev = merged[^1];
            var cur = cols[i];
            var overlap = Math.Min(prev.MaxX, cur.MaxX) - Math.Max(prev.MinX, cur.MinX);
            if (overlap > 1 && overlap <= pack
                && IsSkinnyColumn(prev) != IsSkinnyColumn(cur))
            {
                prev.Ids.AddRange(cur.Ids);
                prev.Centers.AddRange(cur.Centers);
                prev.MinX = Math.Min(prev.MinX, cur.MinX);
                prev.MaxX = Math.Max(prev.MaxX, cur.MaxX);
            }
            else
                merged.Add(cur);
        }
        return merged;
    }

    /// <summary>
    /// Wide portrait boards (400mm carcass, side panels, …) are valid edge
    /// partners even if they sit outside the skinny-strip Y-band.
    /// </summary>
    static void AddWidePortraitColumns(
        List<Column> cols,
        List<NestPlacement> sheet,
        IReadOnlyDictionary<string, Panel> byPanel)
    {
        var owned = new HashSet<string>(cols.SelectMany(c => c.Ids), StringComparer.Ordinal);
        var extras = new List<Column>();
        foreach (var place in sheet)
        {
            if (owned.Contains(place.PanelId)) continue;
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            var w = box.MaxX - box.MinX;
            var h = box.MaxY - box.MinY;
            if (w + 1e-6 < StableColumnMinMm) continue;
            if (h < w * 1.15) continue;
            extras.Add(new Column
            {
                MinX = box.MinX,
                MaxX = box.MaxX,
                Ids = { place.PanelId },
                Centers = { (box.MinX + box.MaxX) * 0.5 },
            });
            owned.Add(place.PanelId);
        }
        foreach (var extra in extras.OrderBy(c => c.Cx))
        {
            var i = cols.FindIndex(c => c.Cx > extra.Cx);
            if (i < 0) cols.Add(extra);
            else cols.Insert(i, extra);
        }
    }

    /// <summary>
    /// Swap the edge skinny run with one wider portrait board. The wider board
    /// (150 / 240 / 400 / side panel) takes the vacated width-edge; the strips
    /// take the board's old X. Opposite-edge partners are skipped so strips
    /// are not dumped onto the other vacuum edge.
    /// </summary>
    static bool TryPairSwap(
        List<NestPlacement> work,
        List<Column> cols,
        int runStart,
        int runEnd,
        bool fromRight,
        Func<string, bool> canMove,
        int sheetIndex,
        IReadOnlyDictionary<string, Panel> byPanel,
        IReadOnlySet<string> portraitIds,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm,
        List<string> reasons)
    {
        var lo = fromRight ? 0 : runEnd + 1;
        var hi = fromRight ? runStart - 1 : cols.Count - 1;
        if (lo > hi) return false;

        var runMin = cols[runStart].MinX;
        var runMax = cols[runEnd].MaxX;
        var runW = runMax - runMin;
        var partners = new List<int>();
        for (var i = lo; i <= hi; i++)
        {
            if (cols[i].Width + 1e-6 < StableColumnMinMm) continue;
            if (cols[i].Ids.Any(id => !canMove(id))) continue;
            if (fromRight && cols[i].MinX <= borderMm + EdgeSlackMm) continue;
            if (!fromRight && cols[i].MaxX >= sheetW - borderMm - EdgeSlackMm) continue;
            var runNewMin = cols[i].MinX;
            var runNewMax = runNewMin + runW;
            if (fromRight && runNewMin <= borderMm + EdgeSlackMm) continue;
            if (!fromRight && runNewMax >= sheetW - borderMm - EdgeSlackMm) continue;
            partners.Add(i);
        }
        partners = partners
            .OrderByDescending(i => cols[i].Width)
            .ThenBy(i => Math.Abs(i - (fromRight ? runStart : runEnd)))
            .ToList();
        if (partners.Count == 0)
        {
            reasons.Add($"pair:no-partner:{(fromRight ? "right" : "left")}");
            return false;
        }

        foreach (var partnerIdx in partners)
        {
            var trial = work.Select(Clone).ToList();
            SwapRunWithPartner(trial, cols, runStart, runEnd, partnerIdx, fromRight);
            UnstickSmalls(
                trial, sheetIndex, byPanel, portraitIds, canMove,
                sheetW, sheetH, borderMm, spacingMm, reasons);
            var why = InvalidWhy(
                trial, work, sheetIndex, byPanel, sheetW, sheetH, borderMm, spacingMm,
                (a, b) => portraitIds.Contains(a) && portraitIds.Contains(b));
            if (why is not null)
            {
                reasons.Add($"pair-reject:{why}:run {runStart}..{runEnd}<->{partnerIdx} w={cols[partnerIdx].Width:0} {string.Join("+", cols[partnerIdx].Ids)}");
                continue;
            }
            var before = Score(work.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
            var after = Score(trial.Where(p => p.SheetIndex == sheetIndex), sizes, kinds, sheetW, borderMm);
            if (after >= before - 1e-6)
            {
                reasons.Add($"pair-reject:score:run {runStart}..{runEnd}<->{partnerIdx} {before:0.000}->{after:0.000}");
                continue;
            }
            reasons.Add($"pair-ok:run {runStart}..{runEnd}<->{partnerIdx} w={cols[partnerIdx].Width:0} {string.Join("+", cols[partnerIdx].Ids)} score {before:0.000}->{after:0.000}");
            work.Clear();
            work.AddRange(trial);
            return true;
        }
        return false;
    }

    static void SwapRunWithPartner(
        List<NestPlacement> work,
        List<Column> cols,
        int runStart,
        int runEnd,
        int partnerIdx,
        bool fromRight)
    {
        var runMin = cols[runStart].MinX;
        var runMax = cols[runEnd].MaxX;
        var partner = cols[partnerIdx];
        var partnerDx = fromRight
            ? runMax - partner.MaxX
            : runMin - partner.MinX;
        var runDx = partner.MinX - runMin;

        void Apply(Column col, double dx)
        {
            if (Math.Abs(dx) < 0.05) return;
            foreach (var id in col.Ids)
            {
                var idx = work.FindIndex(p => p.PanelId == id);
                if (idx < 0) continue;
                var p = work[idx];
                work[idx] = With(p, p.OffsetX + dx, p.OffsetY);
            }
        }

        for (var i = runStart; i <= runEnd; i++)
            Apply(cols[i], runDx);
        Apply(partner, partnerDx);
    }

    /// <summary>
    /// Small parts sitting on / above a portrait column ride that column's X shift.
    /// Assigned by maximum X-overlap, else nearest column centre.
    /// </summary>
    static void AttachSatellites(
        List<Column> cols,
        List<NestPlacement> sheet,
        IReadOnlyDictionary<string, Panel> byPanel,
        List<string> reasons)
    {
        if (cols.Count == 0) return;
        var owned = new HashSet<string>(cols.SelectMany(c => c.Ids), StringComparer.Ordinal);
        var attached = new List<string>();
        foreach (var place in sheet)
        {
            if (owned.Contains(place.PanelId)) continue;
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            var cx = (box.MinX + box.MaxX) * 0.5;
            var best = -1;
            var bestOverlap = 0.0;
            for (var i = 0; i < cols.Count; i++)
            {
                var overlap = Math.Min(box.MaxX, cols[i].MaxX) - Math.Max(box.MinX, cols[i].MinX);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = i;
                }
            }
            if (best < 0 || bestOverlap < 1)
            {
                var bestDist = double.MaxValue;
                for (var i = 0; i < cols.Count; i++)
                {
                    var d = Math.Abs(cols[i].Cx - cx);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }
            }
            if (best < 0) continue;
            var partW = box.MaxX - box.MinX;
            var partH = box.MaxY - box.MinY;
            // Wide rails sitting across several columns cannot ride one column's X shift
            // (they go off-sheet or overlap neighbors). UnstickSmalls moves them after.
            if (partW > cols[best].Width * 1.2 && partW > partH * 0.9)
                continue;
            if (bestOverlap > 1 && bestOverlap < partW * 0.4)
                continue;
            cols[best].Ids.Add(place.PanelId);
            owned.Add(place.PanelId);
            attached.Add($"{place.PanelId}->[{best}]");
        }
        if (attached.Count > 0)
            reasons.Add("satellites=" + string.Join(",", attached));
    }

    static void UnstickSmalls(
        List<NestPlacement> trial,
        int sheetIndex,
        IReadOnlyDictionary<string, Panel> byPanel,
        IReadOnlySet<string> portraitIds,
        Func<string, bool> canMove,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm,
        List<string> reasons)
    {
        for (var n = 0; n < 8; n++)
        {
            var sheet = trial.Where(p => p.SheetIndex == sheetIndex).ToList();
            var hits = NestValidator.FindPolygonCollisions(byPanel.Values.ToList(), sheet, 0);
            if (hits.Count == 0) return;

            var moved = false;
            foreach (var hit in hits)
            {
                foreach (var id in new[] { hit.PanelIdA, hit.PanelIdB })
                {
                    if (!canMove(id) || portraitIds.Contains(id)) continue;
                    if (!byPanel.TryGetValue(id, out var panel)) continue;
                    var idx = trial.FindIndex(p => p.PanelId == id && p.SheetIndex == sheetIndex);
                    if (idx < 0) continue;
                    var cur = trial[idx];
                    var others = trial
                        .Where(p => p.PanelId != id)
                        .Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
                        .ToList();
                    var members = new NestDrag.SlideMember[]
                    {
                        new(id, panel, 0, 0, cur.RotationDeg),
                    };
                    var next = NudgeOffOverlap(
                        members, id, cur.OffsetX, cur.OffsetY,
                        sheetIndex, others, byPanel, sheetW, sheetH, spacingMm, borderMm);
                    if (next is null) continue;
                    var (ox, oy) = next.Value;
                    if (Math.Abs(ox - cur.OffsetX) < 0.2 && Math.Abs(oy - cur.OffsetY) < 0.2)
                        continue;
                    trial[idx] = With(cur, ox, oy);
                    reasons.Add($"unstick:{id} dx={ox - cur.OffsetX:0} dy={oy - cur.OffsetY:0}");
                    moved = true;
                    break;
                }
                if (moved) break;
            }
            if (!moved) return;
        }
    }

    /// <summary>
    /// Cheap unstick: slide left/right at the same Y, then a little down toward
    /// the pack. Never scan the whole sheet (that froze the UI for tens of seconds).
    /// </summary>
    static (double Ox, double Oy)? NudgeOffOverlap(
        NestDrag.SlideMember[] members,
        string id,
        double fromOx,
        double fromOy,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, Panel> byPanel,
        double sheetW,
        double sheetH,
        double spacingMm,
        double borderMm)
    {
        var panel = members[0].Panel;
        var rot = members[0].Rot;
        var min = NestDrag.ClampOnSheet(panel, -1e9, -1e9, rot, sheetW, sheetH, borderMm);
        var max = NestDrag.ClampOnSheet(panel, 1e9, 1e9, rot, sheetW, sheetH, borderMm);

        (double Ox, double Oy) Slide(double toOx, double toOy) =>
            NestDrag.SlideTo(
                members, id, fromOx, fromOy, toOx, toOy,
                sheetIndex, others, byPanel, sheetW, sheetH, spacingMm, borderMm,
                fromOx, fromOy);

        var left = Slide(min.Ox, fromOy);
        var right = Slide(max.Ox, fromOy);
        var yNudge = Math.Max(40, spacingMm * 3);
        var down = Slide(fromOx, fromOy - yNudge);
        var up = Slide(fromOx, fromOy + yNudge);

        (double Ox, double Oy, double Dist)? best = null;
        void Consider((double Ox, double Oy) p)
        {
            var d = Math.Abs(p.Ox - fromOx) + Math.Abs(p.Oy - fromOy);
            if (d < 0.2) return;
            if (best is null || d < best.Value.Dist)
                best = (p.Ox, p.Oy, d);
        }
        Consider(left);
        Consider(right);
        Consider(down);
        Consider(up);
        return best is null ? null : (best.Value.Ox, best.Value.Oy);
    }

    static bool IsSkinnyWidth(double w, double h) =>
        w <= StripMaxShortMm || (h >= w * StripAspectMin && w + 1e-6 < StableColumnMinMm);

    /// <summary>
    /// Translate a consecutive skinny run so it sits at <paramref name="slotIdx"/>,
    /// and shift the columns between toward the edge the run vacated.
    /// </summary>
    static void InsertRun(
        List<NestPlacement> work,
        List<Column> cols,
        int runStart,
        int runEnd,
        int slotIdx,
        int edgeIdx,
        double spacingMm)
    {
        var runMin = cols[runStart].MinX;
        var runPitch = cols[runEnd].MaxX - runMin + spacingMm;

        void Apply(Column col, double dx)
        {
            if (Math.Abs(dx) < 0.05) return;
            foreach (var id in col.Ids)
            {
                var idx = work.FindIndex(p => p.PanelId == id);
                if (idx < 0) continue;
                var p = work[idx];
                work[idx] = With(p, p.OffsetX + dx, p.OffsetY);
            }
        }

        if (runStart > slotIdx)
        {
            for (var i = runStart; i <= runEnd; i++)
                Apply(cols[i], cols[slotIdx].MinX - runMin);
            for (var i = slotIdx; i < runStart; i++)
            {
                if (i != edgeIdx && OverlapsX(cols[i], cols[edgeIdx]))
                    continue;
                Apply(cols[i], runPitch);
            }
        }
        else
        {
            for (var i = runStart; i <= runEnd; i++)
                Apply(cols[i], cols[slotIdx].MinX - runMin);
            for (var i = runEnd + 1; i <= slotIdx; i++)
            {
                if (i != edgeIdx && OverlapsX(cols[i], cols[edgeIdx]))
                    continue;
                Apply(cols[i], -runPitch);
            }
        }
    }

    static double Score(
        IEnumerable<NestPlacement> sheet,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        double sheetW,
        double borderMm)
    {
        var sum = 0.0;
        foreach (var p in sheet)
        {
            if (!sizes.TryGetValue(p.PanelId, out var sz) || !kinds.TryGetValue(p.PanelId, out var kind))
                continue;
            if (kind != Kind.Strip) continue;
            var minX = p.OffsetX;
            var maxX = p.OffsetX + sz.W;
            var onEdge = minX <= borderMm + EdgeSlackMm
                         || maxX >= sheetW - borderMm - EdgeSlackMm;
            if (!onEdge) continue;
            var n = CenterNorm(p, sz.W, sheetW);
            sum += 1 - n;
        }
        return sum;
    }

    static int CountEdgeStrips(
        IEnumerable<NestPlacement> sheet,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        IReadOnlyDictionary<string, Kind> kinds,
        double sheetW,
        double borderMm)
    {
        var n = 0;
        foreach (var p in sheet)
        {
            if (!sizes.TryGetValue(p.PanelId, out var sz) || !kinds.TryGetValue(p.PanelId, out var kind))
                continue;
            if (kind != Kind.Strip) continue;
            var minX = p.OffsetX;
            var maxX = p.OffsetX + sz.W;
            if (minX <= borderMm + EdgeSlackMm || maxX >= sheetW - borderMm - EdgeSlackMm)
                n++;
        }
        return n;
    }

    static double CenterNorm(
        IEnumerable<NestPlacement> sheet,
        IReadOnlyDictionary<string, (double W, double H)> sizes,
        string id,
        double sheetW)
    {
        var p = sheet.First(x => x.PanelId == id);
        return CenterNorm(p, sizes[id].W, sheetW);
    }

    /// <summary>0 at a width-edge, 1 at the sheet centre-line.</summary>
    public static double CenterNorm(NestPlacement p, double partW, double sheetW)
    {
        var cx = p.OffsetX + partW * 0.5;
        var d = Math.Min(cx, sheetW - cx);
        return d / Math.Max(sheetW * 0.5, 1);
    }

    static string? InvalidWhy(
        IReadOnlyList<NestPlacement> trialAll,
        IReadOnlyList<NestPlacement> originalAll,
        int sheetIndex,
        IReadOnlyDictionary<string, Panel> byPanel,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm,
        Func<string, string, bool>? countSpacingHit = null)
    {
        var sheet = trialAll.Where(p => p.SheetIndex == sheetIndex).ToList();
        var origSheet = originalAll.Where(p => p.SheetIndex == sheetIndex).ToList();
        var panels = new List<Panel>();
        foreach (var place in sheet)
        {
            if (!byPanel.TryGetValue(place.PanelId, out var panel)) return "missing-panel";
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            if (box.MinX < borderMm - 0.05 || box.MinY < borderMm - 0.05
                || box.MaxX > sheetW - borderMm + 0.05 || box.MaxY > sheetH - borderMm + 0.05)
                return $"bounds:{place.PanelId}";
            panels.Add(panel);
        }

        var overlap = NestValidator.FindPolygonCollisions(panels, sheet, 0);
        var origOverlap = NestValidator.FindPolygonCollisions(panels, origSheet, 0);
        if (overlap.Count > origOverlap.Count)
            return $"overlap:{overlap.Count}>{origOverlap.Count}:{FormatHits(overlap)}";

        // Spacing among vertical bars must not get worse. Small / landscape
        // remnant parts are step 2 and must not block the strip leaving the edge.
        var origHits = NestValidator.FindPolygonCollisions(panels, origSheet, spacingMm);
        var trialHits = NestValidator.FindPolygonCollisions(panels, sheet, spacingMm);
        if (countSpacingHit is not null)
        {
            origHits = origHits.Where(h => countSpacingHit(h.PanelIdA, h.PanelIdB)).ToList();
            trialHits = trialHits.Where(h => countSpacingHit(h.PanelIdA, h.PanelIdB)).ToList();
        }
        if (trialHits.Count > origHits.Count)
            return $"polygon:{trialHits.Count}>{origHits.Count}:{FormatHits(trialHits)}";
        return null;
    }

    static string FormatHits(IReadOnlyList<NestCollision> hits) =>
        string.Join(",", hits.Take(3).Select(h => $"{h.PanelIdA}×{h.PanelIdB}"));

    static List<NestPlacement> Replace(List<NestPlacement> work, int idx, NestPlacement next)
    {
        var copy = work.Select(Clone).ToList();
        copy[idx] = next;
        return copy;
    }

    static NestPlacement With(NestPlacement p, double ox, double oy) => new()
    {
        PanelId = p.PanelId,
        SheetIndex = p.SheetIndex,
        OffsetX = ox,
        OffsetY = oy,
        RotationDeg = p.RotationDeg,
    };

    static NestPlacement Clone(NestPlacement p) => new()
    {
        PanelId = p.PanelId,
        SheetIndex = p.SheetIndex,
        OffsetX = p.OffsetX,
        OffsetY = p.OffsetY,
        RotationDeg = p.RotationDeg,
    };
}
