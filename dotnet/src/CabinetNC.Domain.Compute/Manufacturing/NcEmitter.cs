namespace CabinetNC.Domain.Manufacturing;

using CabinetNC.Domain.Machines;

/// <summary>Port of src/nc.js opsToNc (G0/G1/F/S — no arcs).</summary>
public static partial class NcEmitter
{
    public static string OpsToNc(
        IEnumerable<CutOp> ops,
        MachineProfile profile,
        IReadOnlyDictionary<string, ToolDefinition>? tools = null,
        PostRecipe? recipe = null)
    {
        var catalog = tools ?? ToolCatalog.DefaultMap();
        var safeZ = profile.SafeZMm;
        var feedXy = profile.FeedXyMmMin;
        var feedZ = profile.FeedZMmMin;
        var rpm = profile.SpindleRpm;
        var list = ops.Where(o => o.Placed && o.Enabled).ToList();
        if (!profile.EnableContour) list = list.Where(o => o.Op is not ("contour" or "pocket")).ToList();
        if (!profile.EnableDrill) list = list.Where(o => o.Op != "drill").ToList();
        if (!profile.EnableGroove) list = list.Where(o => o.Op != "groove").ToList();
        if (recipe is not null)
            return EmitTroy(list, profile, recipe, catalog);

        var contours = list.Where(o => o.Op == "contour" && o.Path is { Count: >= 3 }).ToList();
        var pockets = list.Where(o => o.Op == "pocket" && (
            (o.PathSegments is { Count: > 0 }) ||
            (o.Path is { Count: >= 2 }))).ToList();
        var drills = list.Where(o => o.Op == "drill" && o.SheetX is not null).ToList();
        var grooves = list.Where(o => o.Op == "groove" && o.Path is { Count: >= 2 }).ToList();
        var remnants = list.Where(o => o.Op == "remnant" && o.Path is { Count: >= 2 }).ToList();
        var all = CamSafety.OrderSafe(contours.Concat(pockets).Concat(drills).Concat(grooves).Concat(remnants)).ToList();

        // Prefer first bound tool's spindle over bare machine default when present
        var firstToolId = all.Select(o => o.ToolId).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (firstToolId is not null && catalog.TryGetValue(firstToolId, out var firstTool) && firstTool.SpindleRpm > 0)
            rpm = firstTool.SpindleRpm;

        var lines = new List<string>
        {
            $"(cabinetnc-cut nc · {profile.Id} · {profile.Name} · {profile.Dialect})",
            "(wcs: sheet SW origin · X+ right · Y+ back · Z+ up · units mm)",
            $"(cam safety: drill→tongue→clearance→profile · through+{CamSafety.ThroughAllowanceMm})",
        };
        if (!string.IsNullOrWhiteSpace(profile.OriginNote))
            lines.Add($"(origin: {profile.OriginNote.Replace("(", "").Replace(")", "")})");

        // Single-tool program header (Sheet×Tool export) — explicit feeds, no mixed tools
        var distinctTools = all.Select(o => o.ToolId).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctTools.Count == 1 && catalog.TryGetValue(distinctTools[0]!, out var solo))
        {
            var sheetLabel = all.Count > 0 ? $"S{all[0].SheetIndex + 1}" : "S?";
            lines.Add(
                $"(sheet {sheetLabel} · ToolId={solo.ToolId} · DiameterMm={Fmt(solo.DiameterMm)} · FeedXY={Fmt(solo.FeedXyMmMin)} · FeedZ={Fmt(solo.FeedZMmMin)} · RPM={Math.Round(solo.SpindleRpm)})");
            feedXy = solo.FeedXyMmMin > 0 ? solo.FeedXyMmMin : feedXy;
            feedZ = solo.FeedZMmMin > 0 ? solo.FeedZMmMin : feedZ;
            if (solo.SpindleRpm > 0) rpm = solo.SpindleRpm;
        }

        lines.Add("G21");
        lines.Add("G90");
        if (profile.Dialect == "fanuc_like")
        {
            lines.Add("G17");
            lines.Add("G40");
            lines.Add("G49");
            lines.Add("G80");
        }
        if (rpm > 0) lines.Add($"S{Math.Round(rpm)} M3");
        lines.Add($"G0 Z{Fmt(safeZ)}");

        string? lastTool = null;
        var spindleOn = rpm > 0;
        foreach (var group in all.GroupBy(o => o.SheetIndex).OrderBy(g => g.Key))
        {
            if (distinctTools.Count != 1)
                lines.Add($"(sheet {group.Key + 1})");
            foreach (var item in CamSafety.OrderSafe(group))
            {
                if (!string.IsNullOrWhiteSpace(item.ToolId) && item.ToolId != lastTool)
                {
                    lines.Add($"(tool {item.ToolId})");
                    if (catalog.TryGetValue(item.ToolId, out var def))
                    {
                        feedXy = def.FeedXyMmMin > 0 ? def.FeedXyMmMin : feedXy;
                        feedZ = def.FeedZMmMin > 0 ? def.FeedZMmMin : feedZ;
                        if (def.SpindleRpm > 0 && (lastTool is null || Math.Abs(def.SpindleRpm - rpm) > 1e-6))
                        {
                            rpm = def.SpindleRpm;
                            lines.Add($"S{Math.Round(rpm)} M3");
                            spindleOn = true;
                        }
                    }
                    lastTool = item.ToolId;
                }

                if (item.Op == "contour") EmitContour(lines, item, profile, feedXy, feedZ);
                else if (item.Op == "pocket") EmitPocket(lines, item, profile, feedXy, feedZ);
                else if (item.Op == "drill") EmitDrill(lines, item, profile, feedZ);
                else if (item.Op == "groove") EmitGroove(lines, item, profile, feedXy, feedZ);
                else if (item.Op == "remnant") EmitGroove(lines, item, profile, feedXy, feedZ);
            }
        }

        if (spindleOn) lines.Add("M5");
        var end = (profile.ProgramEnd ?? "M2").ToUpperInvariant();
        lines.Add(end == "M30" ? "M30" : "M2");
        return string.Join("\n", lines) + "\n";
    }

    public static IReadOnlyList<double> ContourPassDepths(double totalDepthMm, double stepdownMm)
    {
        var total = Math.Abs(totalDepthMm);
        var step = Math.Abs(stepdownMm);
        if (total <= 0) return [];
        if (!(step > 0) || step >= total - 1e-9) return [total];
        var depths = new List<double>();
        for (var d = step; d < total - 1e-9; d += step)
            depths.Add(Math.Round(d, 3));
        depths.Add(total);
        return depths;
    }

    static void EmitContour(List<string> lines, CutOp c, MachineProfile profile, double feed, double feedZ)
    {
        var path = c.Path!;
        var safeZ = profile.SafeZMm;
        var total = Math.Abs(c.DepthMm ?? profile.ContourDepthMm);
        var passes = ContourPassDepths(total, profile.ContourStepdownMm);
        lines.Add($"(contour {c.PanelId} tool={c.ToolId ?? "-"} depth={Fmt(total)}{(passes.Count > 1 ? $" passes={passes.Count}" : "")})");
        lines.Add($"G0 X{Fmt(path[0].X)} Y{Fmt(path[0].Y)}");
        for (var p = 0; p < passes.Count; p++)
        {
            var z = -passes[p];
            if (passes.Count > 1) lines.Add($"(pass {p + 1}/{passes.Count} Z{Fmt(z)})");
            lines.Add($"G1 Z{Fmt(z)} F{feedZ}");
            for (var i = 1; i < path.Count; i++)
                lines.Add($"G1 X{Fmt(path[i].X)} Y{Fmt(path[i].Y)} F{feed}");
            if (c.ClosePath)
                lines.Add($"G1 X{Fmt(path[0].X)} Y{Fmt(path[0].Y)} F{feed}");
            if (p < passes.Count - 1) lines.Add($"G0 Z{Fmt(safeZ)}");
        }
        lines.Add($"G0 Z{Fmt(safeZ)}");
    }

    /// <summary>
    /// Pocket: one plunge per Z pass, then stay at cut depth between walls / finish
    /// (Carveco). Retract only between stepdown passes and when the feature ends.
    /// The fill is never closed back to path[0].
    /// </summary>
    static void EmitPocket(List<string> lines, CutOp c, MachineProfile profile, double feed, double feedZ)
    {
        if (c.DepthMm is null or <= 0)
        {
            lines.Add($"(pocket {c.PanelId} BLOCKED missing DepthMm)");
            return;
        }
        if (c.PocketTooSmallForTool)
        {
            lines.Add($"(pocket {c.PanelId} BLOCKED too small for tool)");
            return;
        }
        var safeZ = profile.SafeZMm;
        var total = Math.Abs(c.DepthMm.Value);
        var stepdown = c.StepdownMm is double sd && sd > 0 ? sd : profile.ContourStepdownMm;
        var passes = ContourPassDepths(total, stepdown);
        var segments = c.PathSegments;
        if (segments is null || segments.Count == 0)
        {
            if (c.Path is not { Count: >= 2 }) return;
            lines.Add($"(pocket {c.PanelId} tool={c.ToolId ?? "-"} depth={Fmt(total)} legacy-flat)");
            EmitOpenPolylinePasses(lines, c.Path, passes, safeZ, feed, feedZ);
            lines.Add($"G0 Z{Fmt(safeZ)}");
            return;
        }

        lines.Add($"(pocket {c.PanelId} tool={c.ToolId ?? "-"} depth={Fmt(total)} segments={segments.Count}{(passes.Count > 1 ? $" passes={passes.Count}" : "")})");
        for (var p = 0; p < passes.Count; p++)
        {
            var z = -passes[p];
            if (passes.Count > 1) lines.Add($"(pass {p + 1}/{passes.Count} Z{Fmt(z)})");
            (double X, double Y)? lastCut = null;
            for (var s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                if (seg.Count < 2) continue;
                LinkOrPlunge(lines, lastCut, seg[0], z, safeZ, feed, feedZ);
                for (var i = 1; i < seg.Count; i++)
                    lines.Add($"G1 X{Fmt(seg[i].X)} Y{Fmt(seg[i].Y)} F{feed}");
                lastCut = seg[^1];
            }

            if (c.FinishLoop is { Count: >= 3 } finish)
            {
                lines.Add("(finish)");
                LinkOrPlunge(lines, lastCut, finish[0], z, safeZ, feed, feedZ);
                for (var i = 1; i < finish.Count; i++)
                    lines.Add($"G1 X{Fmt(finish[i].X)} Y{Fmt(finish[i].Y)} F{feed}");
                var last = finish[^1];
                var first = finish[0];
                if (Math.Abs(last.X - first.X) > 1e-6 || Math.Abs(last.Y - first.Y) > 1e-6)
                    lines.Add($"G1 X{Fmt(first.X)} Y{Fmt(first.Y)} F{feed}");
            }

            if (p < passes.Count - 1)
                lines.Add($"G0 Z{Fmt(safeZ)}");
        }
        lines.Add($"G0 Z{Fmt(safeZ)}");
    }

    static void EmitOpenPolylinePasses(
        List<string> lines,
        IReadOnlyList<(double X, double Y)> path,
        IReadOnlyList<double> passes,
        double safeZ,
        double feed,
        double feedZ)
    {
        lines.Add($"G0 X{Fmt(path[0].X)} Y{Fmt(path[0].Y)}");
        for (var p = 0; p < passes.Count; p++)
        {
            var z = -passes[p];
            if (passes.Count > 1) lines.Add($"(pass {p + 1}/{passes.Count} Z{Fmt(z)})");
            lines.Add($"G1 Z{Fmt(z)} F{feedZ}");
            for (var i = 1; i < path.Count; i++)
                lines.Add($"G1 X{Fmt(path[i].X)} Y{Fmt(path[i].Y)} F{feed}");
            if (p < passes.Count - 1) lines.Add($"G0 Z{Fmt(safeZ)}");
        }
    }

    static void EmitDrill(List<string> lines, CutOp d, MachineProfile profile, double feedZ)
    {
        var safeZ = profile.SafeZMm;
        var total = Math.Abs(d.DepthMm ?? 0);
        var peck = Math.Abs(profile.DrillPeckMm);
        lines.Add($"(drill {d.PanelId} dia={d.DiameterMm})");
        lines.Add($"G0 X{Fmt(d.SheetX)} Y{Fmt(d.SheetY)}");
        if (!(peck > 0) || peck >= total - 1e-9)
        {
            lines.Add($"G1 Z{Fmt(-total)} F{feedZ}");
            lines.Add($"G0 Z{Fmt(safeZ)}");
            return;
        }
        for (var z = peck; z < total - 1e-9; z += peck)
        {
            lines.Add($"G1 Z{Fmt(-z)} F{feedZ}");
            lines.Add($"G0 Z{Fmt(safeZ)}");
        }
        lines.Add($"G1 Z{Fmt(-total)} F{feedZ}");
        lines.Add($"G0 Z{Fmt(safeZ)}");
    }

    static void EmitGroove(List<string> lines, CutOp g, MachineProfile profile, double feed, double feedZ)
    {
        if (g.PocketTooSmallForTool)
            return;
        var safeZ = profile.SafeZMm;
        var z = -Math.Abs(g.DepthMm ?? 0);
        lines.Add($"(groove {g.PanelId})");
        if (g.PathSegments is { Count: > 0 } || g.FinishLoop is { Count: >= 3 })
        {
            (double X, double Y)? lastCut = null;
            if (g.PathSegments is { Count: > 0 })
            {
                foreach (var seg in g.PathSegments)
                {
                    if (seg.Count < 2) continue;
                    LinkOrPlunge(lines, lastCut, seg[0], z, safeZ, feed, feedZ);
                    for (var i = 1; i < seg.Count; i++)
                        lines.Add($"G1 X{Fmt(seg[i].X)} Y{Fmt(seg[i].Y)} F{feed}");
                    lastCut = seg[^1];
                }
            }
            if (g.FinishLoop is { Count: >= 3 } finish)
            {
                LinkOrPlunge(lines, lastCut, finish[0], z, safeZ, feed, feedZ);
                for (var i = 1; i < finish.Count; i++)
                    lines.Add($"G1 X{Fmt(finish[i].X)} Y{Fmt(finish[i].Y)} F{feed}");
            }
            lines.Add($"G0 Z{Fmt(safeZ)}");
            return;
        }

        if (g.Path is not { Count: >= 2 } path)
            return;
        lines.Add($"G0 X{Fmt(path[0].X)} Y{Fmt(path[0].Y)}");
        lines.Add($"G1 Z{Fmt(z)} F{feedZ}");
        for (var i = 1; i < path.Count; i++)
            lines.Add($"G1 X{Fmt(path[i].X)} Y{Fmt(path[i].Y)} F{feed}");
        lines.Add($"G0 Z{Fmt(safeZ)}");
    }

    static void LinkOrPlunge(
        List<string> lines,
        (double X, double Y)? lastCut,
        (double X, double Y) next,
        double z,
        double safeZ,
        double feed,
        double feedZ)
    {
        if (lastCut is { } last &&
            Math.Abs(last.X - next.X) < 1e-6 &&
            Math.Abs(last.Y - next.Y) < 1e-6)
            return;
        if (lastCut is not null)
        {
            lines.Add($"G1 X{Fmt(next.X)} Y{Fmt(next.Y)} F{feed}");
            return;
        }

        lines.Add($"G0 Z{Fmt(safeZ)}");
        lines.Add($"G0 X{Fmt(next.X)} Y{Fmt(next.Y)}");
        lines.Add($"G1 Z{Fmt(z)} F{feedZ}");
    }

    static string Fmt(double? n) => (Math.Round(n ?? 0, 3)).ToString("0.###");
}
