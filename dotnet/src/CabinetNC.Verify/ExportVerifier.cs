namespace CabinetNC.Verify;

using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using Clipper2Lib;

/// <summary>
/// Independent 计码 gate: compares CAD intent (panels + nest placement) with the material
/// the emitted G-code actually removes. Sees no CutOp, no planner output. Rules key on
/// <see cref="VerifyCodes"/>.
/// </summary>
public sealed class ExportVerifier : IExportVerifier
{
    public const string EngineId = "cabinetnc.verify/sweep-2.5d";
    const int MaxPiecesPerRule = 6;

    readonly VerifyTolerances _tol;

    public ExportVerifier(VerifyTolerances? tolerances = null) =>
        _tol = tolerances ?? VerifyTolerances.Default;

    public VerifyReport Verify(VerifyInput input)
    {
        var issues = new List<VerifyIssue>();
        var sweep = new NcSweep(input.Programs, input.Tools, input.ZDatum, _tol);
        foreach (var t in sweep.UnknownTools.OrderBy(t => t))
        {
            issues.Add(Issue(VerifyCodes.ToolUnknown, VerifyIssue.Error, "-", null, 0, 0,
                $"程序使用 T{t}，刀库无此刀径，无法扫掠", input.SheetIndex));
        }

        var panels = IntentModel.Build(input.Panels, input.Placements, input.SheetIndex, _tol);
        if (panels.Count == 0)
        {
            issues.Add(Issue(VerifyCodes.NoPanels, VerifyIssue.Warn, "-", null, 0, 0,
                $"S{input.SheetIndex + 1} 无已排板件，验算跳过", input.SheetIndex));
            return Report(input, issues, panels, sweep);
        }

        foreach (var p in panels)
            VerifyPanel(p, sweep, input, issues);

        StrayCuts(panels, sweep, input, issues);
        return Report(input, issues, panels, sweep);
    }

    VerifyReport Report(VerifyInput input, List<VerifyIssue> issues, IReadOnlyList<PanelIntent> panels, NcSweep sweep) =>
        new()
        {
            Ok = issues.All(i => !i.IsError),
            Issues = issues,
            SheetIndex = input.SheetIndex,
            PanelCount = panels.Count,
            FeatureCount = panels.Sum(p => p.Features.Count),
            StrokeCount = sweep.StrokeCount,
            Engine = EngineId,
        };

    // ------------------------------------------------------------------ panel rules

    void VerifyPanel(PanelIntent p, NcSweep sweep, VerifyInput input, List<VerifyIssue> issues)
    {
        var sheet = input.SheetIndex;
        var th = p.ThicknessMm;
        if (th <= 0 || p.Outline.Count == 0)
        {
            issues.Add(Issue(VerifyCodes.PanelUnverifiable, VerifyIssue.Warn, p.PanelId, null, 0, 0,
                th <= 0 ? "板厚为 0，无法判定深度" : "外轮廓退化", sheet));
            return;
        }

        var F = p.Outline;
        var insideF = Geo.Offset(F, -_tol.XyMm, _tol.ArcChordMm);
        var removedThrough = sweep.RemovedToDepth(th, th);

        // Features we can reason about vs. ones we only tolerate (still count as "allowed" area).
        var active = new List<FeatureIntent>();
        foreach (var f in p.Features)
        {
            if (f.Unverifiable is not null)
            {
                issues.Add(Issue(VerifyCodes.FeatureUnverifiable, VerifyIssue.Warn, p.PanelId, f.FeatureId,
                    CentreOf(f.Allowed), $"{f.Kind}/{f.FeatureId}: {f.Unverifiable}，未验算", sheet));
            }
            if (f.Allowed.Count == 0) continue;
            var onPanel = Geo.AreaMm2(Geo.Inter(f.Allowed, F));
            if (onPanel < 0.01 * Math.Max(1e-9, Geo.AreaMm2(f.Allowed)))
            {
                issues.Add(Issue(VerifyCodes.FeatureOffPanel, VerifyIssue.Warn, p.PanelId, f.FeatureId,
                    CentreOf(f.Allowed), $"{f.Kind}/{f.FeatureId}: 特征位于板外，未验算", sheet));
                continue;
            }
            active.Add(f);
        }

        // --- outline must be cut through all around (except declared tabs)
        OutlineUndercut(p, F, removedThrough, input, sweep, issues);

        // --- each feature: opening / floor present
        foreach (var f in active.Where(f => f.IsVerifiable))
        {
            if (f.Kind == IntentKind.Hole)
            {
                HoleRule(p, f, F, insideF, removedThrough, sweep, input, issues);
                continue;
            }
            if (f.Through)
                ThroughFeatureUndercut(p, f, removedThrough, input, sweep, issues, emit: true);
            else
                BlindFloor(p, f, insideF, sweep, issues, emit: true);

            if (f.Kind == IntentKind.Groove && f.Centerline is not null && f.WidthMm is double w)
                GrooveWidth(p, f, w, insideF, removedThrough, sweep, issues);
        }

        // --- nothing may be removed where no feature allows it, at every depth level
        OvercutLevels(p, F, insideF, active, removedThrough, sweep, input, issues);
    }

    void OutlineUndercut(
        PanelIntent p, Paths64 F, Paths64 removedThrough, VerifyInput input, NcSweep sweep, List<VerifyIssue> issues)
    {
        var band = Geo.Diff(
            Geo.Offset(F, _tol.XyMm + _tol.EdgeBandMm, _tol.ArcChordMm),
            Geo.Offset(F, _tol.XyMm, _tol.ArcChordMm));
        band = Geo.Diff(band, BridgeDiscs(p, null, input, sweep));
        if (band.Count == 0) return;

        var uncut = Geo.Diff(band, removedThrough);
        var bandArea = Geo.AreaMm2(band);
        var uncutArea = Geo.AreaMm2(uncut);
        if (uncutArea >= 0.95 * bandArea)
        {
            issues.Add(Issue(VerifyCodes.ThroughUndercut, VerifyIssue.Error, p.PanelId, null, CentreOf(F),
                "外轮廓没有切穿（无贯通刀路）", input.SheetIndex) with { AreaMm2 = Round(uncutArea) });
            return;
        }
        foreach (var piece in Geo.Pieces(uncut, _tol.MinAreaMm2).Take(MaxPiecesPerRule))
        {
            var (x, y) = Geo.Centroid(piece);
            var len = Geo.AreaMm2(piece) / _tol.EdgeBandMm;
            issues.Add(Issue(VerifyCodes.ThroughUndercut, VerifyIssue.Error, p.PanelId, null, x, y,
                $"外轮廓 ({x:0.#},{y:0.#}) 附近约 {len:0.#}mm 未切穿（非声明桥位）", input.SheetIndex)
                with { MeasuredMm = Round(len), AreaMm2 = Round(Geo.AreaMm2(piece)) });
        }
    }

    /// <returns>True when the opening is fully cut.</returns>
    bool ThroughFeatureUndercut(
        PanelIntent p, FeatureIntent f, Paths64 removedThrough, VerifyInput input, NcSweep sweep,
        List<VerifyIssue> issues, bool emit)
    {
        var expected = Reachable(f.Core, sweep);
        var inner = Geo.Offset(expected, -_tol.XyMm, _tol.ArcChordMm);
        if (inner.Count == 0) inner = expected;
        var band = Geo.Diff(inner, Geo.Offset(expected, -(_tol.XyMm + _tol.EdgeBandMm), _tol.ArcChordMm));
        if (band.Count == 0 || Geo.AreaMm2(band) < 1e-6) band = inner;
        band = Geo.Diff(band, BridgeDiscs(p, f.FeatureId, input, sweep));
        if (band.Count == 0) return true;

        var uncut = Geo.Diff(band, removedThrough);
        var uncutArea = Geo.AreaMm2(uncut);
        if (uncutArea <= 1e-6) return true;
        var pieces = Geo.Pieces(uncut, MinPiece(Geo.AreaMm2(band))).ToList();
        if (uncutArea < 0.95 * Geo.AreaMm2(band) && pieces.Count == 0) return true;
        if (!emit) return false;

        var label = f.Kind == IntentKind.Hole ? "通孔" : "开窗/镂空";
        if (uncutArea >= 0.95 * Geo.AreaMm2(band))
        {
            issues.Add(Issue(VerifyCodes.FeatureNotCut, VerifyIssue.Error, p.PanelId, f.FeatureId, CentreOf(f.Core),
                $"{label} {f.FeatureId} 完全没有贯通刀路", input.SheetIndex) with { AreaMm2 = Round(uncutArea) });
            return false;
        }
        foreach (var piece in pieces.Take(MaxPiecesPerRule))
        {
            var (x, y) = Geo.Centroid(piece);
            var len = Geo.AreaMm2(piece) / _tol.EdgeBandMm;
            issues.Add(Issue(VerifyCodes.ThroughUndercut, VerifyIssue.Error, p.PanelId, f.FeatureId, x, y,
                $"{label} {f.FeatureId} ({x:0.#},{y:0.#}) 约 {len:0.#}mm 未切穿", input.SheetIndex)
                with { MeasuredMm = Round(len), AreaMm2 = Round(Geo.AreaMm2(piece)) });
        }
        return false;
    }

    /// <returns>True when the floor is fully cleared.</returns>
    bool BlindFloor(PanelIntent p, FeatureIntent f, Paths64 insideF, NcSweep sweep, List<VerifyIssue> issues, bool emit)
    {
        if (f.DepthMm is not double d || f.Core.Count == 0) return true;
        var core = Geo.Inter(Geo.Offset(Reachable(f.Core, sweep), -_tol.XyMm, _tol.ArcChordMm), insideF);
        if (core.Count == 0) return true;
        var removed = sweep.RemovedToDepth(d, p.ThicknessMm);
        var uncut = Geo.Diff(core, removed);
        var coreArea = Geo.AreaMm2(core);
        var uncutArea = Geo.AreaMm2(uncut);
        if (uncutArea <= 1e-6) return true;
        var pieces = Geo.Pieces(uncut, MinPiece(coreArea)).ToList();
        if (uncutArea < 0.95 * coreArea && pieces.Count == 0) return true;
        if (!emit) return false;

        var what = f.Kind switch
        {
            IntentKind.Groove => "槽",
            IntentKind.Hole => "杯孔",
            _ => "口袋",
        };
        if (uncutArea >= 0.95 * coreArea)
        {
            issues.Add(Issue(VerifyCodes.FeatureNotCut, VerifyIssue.Error, p.PanelId, f.FeatureId, CentreOf(f.Core),
                $"{what} {f.FeatureId} 深 {d:0.##} 没有任何刀路到达", p.Placement.SheetIndex)
                with { ExpectedMm = d, AreaMm2 = Round(uncutArea) });
            return false;
        }
        var code = f.Kind == IntentKind.Groove ? VerifyCodes.GrooveFloorUncut : VerifyCodes.PocketFloorUncut;
        foreach (var piece in pieces.Take(MaxPiecesPerRule))
        {
            var (x, y) = Geo.Centroid(piece);
            var gap = Geo.GapMm(piece);
            var area = Geo.AreaMm2(piece);
            issues.Add(Issue(code, VerifyIssue.Error, p.PanelId, f.FeatureId, x, y,
                $"{what} {f.FeatureId} 深 {d:0.##} 在 ({x:0.#},{y:0.#}) 残留 {area:0.#}mm²（最宽 {gap:0.##}mm 未清）",
                p.Placement.SheetIndex) with { MeasuredMm = gap, ExpectedMm = 0, AreaMm2 = Round(area) });
        }
        return false;
    }

    void GrooveWidth(
        PanelIntent p, FeatureIntent f, double width, Paths64 insideF, Paths64 removedThrough, NcSweep sweep,
        List<VerifyIssue> issues)
    {
        if (f.Centerline is not { Count: >= 2 } line) return;
        var removed = f.Through ? removedThrough : sweep.RemovedToDepth(f.DepthMm ?? 0, p.ThicknessMm);
        var halfLen = width / 2 + _tol.GrooveWidthMm + 1.0;
        const double halfThick = 0.05;

        var total = 0d;
        for (var i = 1; i < line.Count; i++) total += Dist(line[i - 1], line[i]);
        if (total < 1) return;
        var samples = Math.Clamp((int)Math.Ceiling(total / 5), 3, 40);

        // Grooves crossing this one (T/L/+ junctions): their swept union is
        // wider than this groove's own width by design, so samples falling
        // inside a sibling's swept region are not a mismatch.
        var crossings = p.Features
            .Where(g => g.FeatureId != f.FeatureId && g.Kind == IntentKind.Groove && g.Allowed is { Count: > 0 })
            .Select(g => g.Allowed)
            .ToList();

        double worst = 0, worstMeasured = width, wx = 0, wy = 0;
        for (var s = 0; s < samples; s++)
        {
            var along = total * (s + 0.5) / samples;
            if (!PointAlong(line, along, out var x, out var y, out var ux, out var uy)) continue;
            if (!Geo.Contains(insideF, x, y)) continue;
            var probe = Geo.Probe(x, y, -uy, ux, halfLen, halfThick);
            if (crossings.Any(c => Geo.Inter(probe, c) is { Count: > 0 })) continue;
            var measured = Geo.AreaMm2(Geo.Inter(probe, removed)) / (2 * halfThick);
            var dev = measured - width;
            if (Math.Abs(dev) > Math.Abs(worst))
            {
                worst = dev;
                worstMeasured = measured;
                wx = x;
                wy = y;
            }
        }
        if (Math.Abs(worst) <= _tol.GrooveWidthMm) return;
        var how = worst > 0 ? "宽" : "窄";
        issues.Add(Issue(VerifyCodes.GrooveWidthMismatch, VerifyIssue.Error, p.PanelId, f.FeatureId, wx, wy,
            $"槽 {f.FeatureId} 在 ({wx:0.#},{wy:0.#}) 实切宽 {worstMeasured:0.##} vs CAD {width:0.##}（{how} {Math.Abs(worst):0.##}mm）",
            p.Placement.SheetIndex) with { MeasuredMm = Round(worstMeasured), ExpectedMm = width });
    }

    void HoleRule(
        PanelIntent p, FeatureIntent f, Paths64 F, Paths64 insideF, Paths64 removedThrough, NcSweep sweep,
        VerifyInput input, List<VerifyIssue> issues)
    {
        var milled = new List<VerifyIssue>();
        var ok = f.Through
            ? ThroughFeatureUndercut(p, f, removedThrough, input, sweep, milled, emit: true)
            : BlindFloor(p, f, insideF, sweep, milled, emit: true);
        if (ok) return;

        var (cx, cy) = f.HoleCentre ?? CentreOf(f.Core);
        var expectedDia = f.HoleDiameterMm ?? 0;
        NcSweep.Plunge? nearest = null;
        var nearestD = double.PositiveInfinity;
        foreach (var pl in sweep.Plunges)
        {
            var d = Math.Sqrt((pl.X - cx) * (pl.X - cx) + (pl.Y - cy) * (pl.Y - cy));
            if (d < nearestD)
            {
                nearestD = d;
                nearest = pl;
            }
        }

        if (nearest is not null && nearestD <= _tol.DrillCentreMm)
        {
            var toolDia = nearest.RadiusMm * 2;
            if (expectedDia > 0 && Math.Abs(toolDia - expectedDia) > _tol.DrillDiameterMm)
            {
                issues.Add(Issue(VerifyCodes.HoleDiameterMismatch, VerifyIssue.Error, p.PanelId, f.FeatureId, cx, cy,
                    $"孔 {f.FeatureId} Ø{expectedDia:0.##} 用 T{nearest.ToolNum} Ø{toolDia:0.##} 钻，直径不符",
                    input.SheetIndex) with { MeasuredMm = toolDia, ExpectedMm = expectedDia });
            }
            var depth = DeepestPlunge(sweep, cx, cy, p.ThicknessMm);
            var want = f.Through ? p.ThicknessMm : f.DepthMm ?? p.ThicknessMm;
            var tooShallow = depth < want - _tol.ZMm;
            var tooDeep = !f.Through && depth > want + _tol.ZMm;
            if (tooShallow || tooDeep)
            {
                issues.Add(Issue(VerifyCodes.DepthMismatch, VerifyIssue.Error, p.PanelId, f.FeatureId, cx, cy,
                    $"孔 {f.FeatureId} 钻深 {depth:0.##} vs 要求 {want:0.##}{(f.Through ? "（贯通）" : "")}",
                    input.SheetIndex) with { MeasuredMm = Round(depth), ExpectedMm = want });
            }
            return;
        }

        if (nearest is not null && nearestD <= _tol.DrillDriftSearchMm)
        {
            issues.Add(Issue(VerifyCodes.DrillCentreDrift, VerifyIssue.Error, p.PanelId, f.FeatureId, cx, cy,
                $"孔 {f.FeatureId} 圆心 ({cx:0.##},{cy:0.##}) 最近下刀点偏 {nearestD:0.##}mm",
                input.SheetIndex) with { MeasuredMm = Round(nearestD), ExpectedMm = 0 });
            return;
        }

        issues.AddRange(milled);
    }

    void OvercutLevels(
        PanelIntent p, Paths64 F, Paths64 insideF, List<FeatureIntent> active, Paths64 removedThrough,
        NcSweep sweep, VerifyInput input, List<VerifyIssue> issues)
    {
        var th = p.ThicknessMm;
        var levels = new SortedSet<double> { _tol.SurfaceDepthMm };
        foreach (var f in active.Where(f => !f.Through && f.DepthMm is > 0))
        {
            var deeper = f.DepthMm!.Value + 2 * _tol.ZMm;
            if (deeper < th - _tol.ZMm) levels.Add(deeper);
        }
        levels.Add(th);

        var reported = new Paths64();
        foreach (var L in levels)
        {
            var isThrough = L >= th - 1e-9;
            var allowed = Geo.Union(active
                .Where(f => f.Through || f.DepthMm is null || f.DepthMm.Value >= L - _tol.ZMm)
                .Select(f => f.Allowed));
            var removed = isThrough ? removedThrough : sweep.RemovedToDepth(L, th);
            var over = Geo.Diff(Geo.Inter(removed, insideF), Geo.Offset(allowed, _tol.XyMm, _tol.ArcChordMm));
            if (reported.Count > 0)
                over = Geo.Diff(over, Geo.Offset(reported, _tol.XyMm, _tol.ArcChordMm));

            foreach (var piece in Geo.Pieces(over, _tol.MinAreaMm2).Take(MaxPiecesPerRule))
            {
                var (x, y) = Geo.Centroid(piece);
                var area = Geo.AreaMm2(piece);
                var pieceRegion = new Paths64 { piece };
                reported.AddRange(pieceRegion);
                var actual = sweep.MaxDepthInside(pieceRegion, th) ?? L;

                if (isThrough || actual >= th - _tol.ZMm)
                {
                    var near = NearestFeature(active, x, y, F);
                    issues.Add(Issue(VerifyCodes.ThroughOvercut, VerifyIssue.Error, p.PanelId, near?.FeatureId, x, y,
                        $"板体在 ({x:0.#},{y:0.#}) 被切穿 {area:0.#}mm²{Near(near)}", input.SheetIndex)
                        with { AreaMm2 = Round(area), MeasuredMm = th });
                    continue;
                }

                var host = active.FirstOrDefault(f =>
                    !f.Through && f.DepthMm is double fd && fd < L - _tol.ZMm
                    && Geo.Contains(Geo.Offset(f.Allowed, _tol.XyMm, _tol.ArcChordMm), x, y));
                if (host is not null)
                {
                    issues.Add(Issue(VerifyCodes.DepthMismatch, VerifyIssue.Error, p.PanelId, host.FeatureId, x, y,
                        $"{host.Kind} {host.FeatureId} 在 ({x:0.#},{y:0.#}) 切到 {actual:0.##} 深，CAD 为 {host.DepthMm:0.##}",
                        input.SheetIndex) with { MeasuredMm = Round(actual), ExpectedMm = host.DepthMm, AreaMm2 = Round(area) });
                    continue;
                }

                var nearest = NearestFeature(active, x, y, F);
                issues.Add(Issue(VerifyCodes.BlindOvercut, VerifyIssue.Error, p.PanelId, nearest?.FeatureId, x, y,
                    $"板面在 ({x:0.#},{y:0.#}) 被切 {actual:0.##} 深 {area:0.#}mm²，无对应特征{Near(nearest)}",
                    input.SheetIndex) with { MeasuredMm = Round(actual), AreaMm2 = Round(area) });
            }
        }
    }

    void StrayCuts(IReadOnlyList<PanelIntent> panels, NcSweep sweep, VerifyInput input, List<VerifyIssue> issues)
    {
        var thMax = panels.Max(p => p.ThicknessMm);
        if (thMax <= 0) return;
        var removed = sweep.RemovedAny(thMax);
        if (removed.Count == 0) return;
        var slack = 2 * sweep.MaxToolRadiusMm + _tol.OutlineKerfSlackMm;
        var envelope = Geo.Union(panels.Select(p => Geo.Offset(p.Outline, slack, _tol.ArcChordMm)));
        var stray = Geo.Diff(removed, envelope);
        foreach (var piece in Geo.Pieces(stray, _tol.MinAreaMm2).Take(MaxPiecesPerRule))
        {
            var (x, y) = Geo.Centroid(piece);
            issues.Add(Issue(VerifyCodes.StrayCut, VerifyIssue.Warn, "-", null, x, y,
                $"({x:0.#},{y:0.#}) 附近有 {Geo.AreaMm2(piece):0.#}mm² 刀路不属于任何板件（余料线或游走）",
                input.SheetIndex) with { AreaMm2 = Round(Geo.AreaMm2(piece)) });
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Smallest defect worth reporting: absolute floor, but never more than 20% of a small feature.</summary>
    double MinPiece(double regionAreaMm2) =>
        Math.Max(0.05, Math.Min(_tol.MinAreaMm2, 0.2 * regionAreaMm2));

    /// <summary>
    /// The part of <paramref name="core"/> a round cutter can actually reach: sharp CAD inside
    /// corners become R fillets. Uses the smallest mill that cut there; falls back to the
    /// core itself when no tool fits (so a missing/too-big-tool feature still fails).
    /// </summary>
    Paths64 Reachable(Paths64 core, NcSweep sweep)
    {
        var r = sweep.MillRadiusInside(core) ?? sweep.MaxMillRadiusMm;
        if (r <= 1e-9) return core;
        var opened = Geo.Offset(Geo.Offset(core, -r, _tol.ArcChordMm), r, _tol.ArcChordMm);
        return opened.Count == 0 || Geo.AreaMm2(opened) < 1e-6 ? core : opened;
    }

    Paths64 BridgeDiscs(PanelIntent p, string? featureId, VerifyInput input, NcSweep sweep)
    {
        var discs = new List<Paths64>();
        foreach (var b in input.Bridges)
        {
            if (b.SheetIndex != input.SheetIndex) continue;
            if (!string.Equals(b.PanelId, p.PanelId, StringComparison.Ordinal)) continue;
            if (!string.Equals(b.FeatureId ?? "", featureId ?? "", StringComparison.Ordinal)) continue;
            var r = Math.Max(0.5, b.WidthMm) / 2 + sweep.MaxToolRadiusMm + _tol.XyMm + 1.0;
            discs.Add(Geo.Disc(b.X, b.Y, r));
        }
        return discs.Count == 0 ? [] : Geo.Union(discs);
    }

    static double DeepestPlunge(NcSweep sweep, double cx, double cy, double th)
    {
        var best = double.NegativeInfinity;
        foreach (var pl in sweep.Plunges)
        {
            if (Math.Abs(pl.X - cx) > 0.6 || Math.Abs(pl.Y - cy) > 0.6) continue;
            var d = sweep.DepthOf(pl.Z, th);
            if (d > best) best = d;
        }
        return double.IsNegativeInfinity(best) ? 0 : best;
    }

    static FeatureIntent? NearestFeature(List<FeatureIntent> active, double x, double y, Paths64 F)
    {
        FeatureIntent? best = null;
        var bestD = Geo.DistanceToBoundary(F, x, y);
        foreach (var f in active)
        {
            if (f.Allowed.Count == 0) continue;
            var d = Geo.DistanceToBoundary(f.Allowed, x, y);
            if (d < bestD)
            {
                bestD = d;
                best = f;
            }
        }
        return best;
    }

    static string Near(FeatureIntent? f) => f is null ? "（最近为外轮廓）" : $"（最近特征 {f.FeatureId}）";

    static (double X, double Y) CentreOf(Paths64 polys)
    {
        if (polys.Count == 0) return (0, 0);
        var (minX, minY, maxX, maxY) = Geo.Bounds(polys);
        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }

    static VerifyIssue Issue(string code, string level, string panelId, string? featureId, (double X, double Y) at, string msg, int sheet) =>
        Issue(code, level, panelId, featureId, at.X, at.Y, msg, sheet);

    static VerifyIssue Issue(string code, string level, string panelId, string? featureId, double x, double y, string msg, int sheet) =>
        new(code, level, panelId, featureId, Math.Round(x, 3), Math.Round(y, 3), msg) { SheetIndex = sheet };

    static double Round(double v) => Math.Round(v, 3);

    static double Dist(Point64 a, Point64 b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy) / Geo.Scale;
    }

    static bool PointAlong(Path64 line, double alongMm, out double x, out double y, out double ux, out double uy)
    {
        x = y = ux = uy = 0;
        var remaining = alongMm;
        for (var i = 1; i < line.Count; i++)
        {
            var d = Dist(line[i - 1], line[i]);
            if (d < 1e-9) continue;
            if (remaining <= d)
            {
                var t = remaining / d;
                x = (line[i - 1].X + (line[i].X - line[i - 1].X) * t) / Geo.Scale;
                y = (line[i - 1].Y + (line[i].Y - line[i - 1].Y) * t) / Geo.Scale;
                ux = (line[i].X - line[i - 1].X) / (d * Geo.Scale);
                uy = (line[i].Y - line[i - 1].Y) / (d * Geo.Scale);
                return true;
            }
            remaining -= d;
        }
        return false;
    }
}
