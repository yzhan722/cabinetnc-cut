using System.Globalization;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Xunit.Abstractions;

namespace CabinetNC.Domain.Tests.Regression;

/// <summary>
/// Machine-safety invariants every emitted program must satisfy, independent of the
/// exact toolpath text. Goldens catch "the output changed"; these catch "the output
/// became dangerous" and name the rule that broke, so a post-processor change can be
/// reviewed against docs/sprint/POST_CHANGE_CHECKLIST.md.
/// </summary>
public class NcSafetyInvariantTests(ITestOutputHelper output)
{
    /// <summary>Software may cut this far into the spoilboard below the sheet (dry-run checklist).</summary>
    const double SpoilboardAllowanceMm = 1.0;

    /// <summary>Cutter-centre paths may leave the placed panel by two radii (compensation + entry arc) plus slack.</summary>
    const double OutsidePanelSlackMm = 2.0;

    public static IEnumerable<object[]> Jobs()
    {
        yield return [GoldenFixtures.TroySingleFileAtc()];
        yield return [GoldenFixtures.SheetToolSinglePanel()];
        yield return [GoldenFixtures.MultiMaterialNoShare()];
        yield return [ShopMix("troy")];
        yield return [ShopMix("sheet_tool")];
    }

    [Theory]
    [MemberData(nameof(Jobs))]
    public void Every_program_satisfies_machine_safety_invariants(GoldenJob job)
    {
        var p = GoldenJobRunner.Prepare(job);
        Assert.True(p.Preflight.Ok, $"{job.Id}: preflight must pass for a safety fixture: "
            + string.Join(",", p.Preflight.Issues.Select(i => i.Code)));

        var programs = GoldenJobRunner.EmitPrograms(job, p);
        Assert.NotEmpty(programs);
        foreach (var prog in programs)
        {
            var violations = Check(job, p, prog.ToolId, prog.NcText, out var strokes);
            output.WriteLine($"{job.Id}/{prog.Name}: {strokes} strokes, {violations.Count} violation(s)");
            Assert.True(violations.Count == 0,
                $"{job.Id}/{prog.Name} breaks {violations.Count} safety invariant(s):\n  " + string.Join("\n  ", violations));
        }
    }

    // --- mutation checks: prove the checker sees each class of danger -----------------

    [Fact]
    public void Checker_flags_rapid_xy_below_safe_z()
    {
        var (job, p, nc) = TroyProgram();
        var mutated = ReplaceOnce(nc, "G0 Z30.0000\nG1 Z-0.5500", "G0 Z5.0000\nG0 X50.0000 Y50.0000\nG1 Z-0.5500");
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("rapid XY below safe Z", StringComparison.Ordinal));
        Assert.Contains(v, s => s.StartsWith("rapid ends inside the material zone", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_flags_cut_through_the_spoilboard()
    {
        var (job, p, nc) = TroyProgram();
        var mutated = nc.Replace("G1 Z-0.5500", "G1 Z-3.0000", StringComparison.Ordinal);
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("cut deeper than spoilboard allowance", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_flags_cut_before_spindle_on()
    {
        var (job, p, nc) = TroyProgram();
        var mutated = nc.Replace("M3 S14500", "M3 S0", StringComparison.Ordinal);
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("cut before spindle on", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_flags_feed_not_from_recipe_and_plunge_at_cutting_feed()
    {
        var (job, p, nc) = TroyProgram();
        var mutated = ReplaceOnce(nc, "G1 Z0.5000 F1000.0", "G1 Z0.5000 F7777.0");
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("feed F7777 is not a recipe value", StringComparison.Ordinal));
        Assert.Contains(v, s => s.StartsWith("plunge at cutting feed", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_flags_cut_outside_every_placed_panel()
    {
        var (job, p, nc) = TroyProgram();
        var mutated = ReplaceOnce(nc, "G1 Y165.0000 F20000.0", "G1 Y900.0000 F20000.0");
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("cut outside every placed panel", StringComparison.Ordinal));
    }

    [Fact]
    public void Checker_flags_missing_program_end_and_tool_left_low()
    {
        var (job, p, nc) = TroyProgram();
        var cut = nc.IndexOf("G0 Z30.0000\nG0 X0.0000 Y0.0000\nG80", StringComparison.Ordinal);
        Assert.True(cut > 0, "expected the Troy tail (retract, home, G80) in the program");
        var mutated = nc[..cut];
        var v = Check(job, p, null, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("program ends with the tool below safe Z", StringComparison.Ordinal));
        Assert.Contains(v, s => s == "no end of program (M30/M2)");
    }

    [Fact]
    public void Checker_flags_m6_inside_a_sheet_tool_program()
    {
        var job = GoldenFixtures.SheetToolSinglePanel();
        var p = GoldenJobRunner.Prepare(job);
        var prog = GoldenJobRunner.EmitPrograms(job, p).First(x => x.ToolId == "T2");
        var mutated = ReplaceOnce(NcTextNormalizer.Normalize(prog.NcText), "(tool T2)", "(tool T2)\nM6 T2");
        var v = Check(job, p, prog.ToolId, mutated, out _);
        Assert.Contains(v, s => s.StartsWith("Sheet×Tool program contains M6", StringComparison.Ordinal));
    }

    // --- the checker -------------------------------------------------------------------

    static List<string> Check(GoldenJob job, GoldenPipeline p, string? toolId, string nc, out int strokeCount)
    {
        var troy = job.Post == "troy";
        var recipe = PostRecipe.TroyDefault();
        var tools = ToolCatalog.DefaultMap();
        var maxThickness = job.Panels.Max(x => x.ThicknessMm);
        var placed = PlacedBounds(job, p.Nest);
        var replay = OsaiTroyParser.Replay(nc);
        strokeCount = replay.Strokes.Count;
        var violations = new List<string>();

        // Coordinate frame differs per post: Troy has Z0 at the board bottom, the
        // generic Sheet×Tool post has Z0 at the sheet top.
        var safeZ = troy ? recipe.SafeZMm : p.Profile.SafeZMm;
        var materialTopZ = troy ? maxThickness : 0;
        var deepestAllowedZ = troy ? -SpoilboardAllowanceMm : -(maxThickness + SpoilboardAllowanceMm);

        var tool = toolId is not null && tools.TryGetValue(toolId, out var t) ? t : null;
        if (!troy && tool is null)
        {
            violations.Add($"Sheet×Tool program for unknown tool '{toolId}'");
            return violations;
        }

        var plungeFeeds = troy
            ? new HashSet<double>
            {
                recipe.TonguePlunge, recipe.ClearancePlunge, recipe.ProfileFirstPlunge,
                recipe.ProfileLastPlunge, recipe.DrillPlunge, recipe.GuillotinePlunge,
            }
            : [tool!.FeedZMmMin];
        var allowedFeeds = new HashSet<double>(plungeFeeds);
        if (troy)
            allowedFeeds.UnionWith([recipe.TongueFeed, recipe.ClearanceFeed, recipe.ProfileFirstFeed, recipe.ProfileLastFeed, recipe.GuillotineFeed]);
        else
            allowedFeeds.Add(tool!.FeedXyMmMin);
        var allowedRpm = troy
            ? new HashSet<double> { recipe.TongueRpm, recipe.ClearanceRpm, recipe.ProfileFirstRpm, recipe.ProfileLastRpm, recipe.DrillRpm }
            : [tool!.SpindleRpm];
        var source = troy ? "recipe" : "ToolCatalog " + toolId;

        if (replay.Strokes.Count == 0)
        {
            violations.Add("no motion at all");
            return violations;
        }

        for (var i = 0; i < replay.Strokes.Count; i++)
        {
            var s = replay.Strokes[i];
            var where = $"stroke {i + 1} ({Fmt(s.X0)},{Fmt(s.Y0)},{Fmt(s.Z0)})→({Fmt(s.X1)},{Fmt(s.Y1)},{Fmt(s.Z1)})";
            var lowZ = Math.Min(s.Z0, s.Z1);

            if (s.Rapid)
            {
                // The parser starts at Z=0 because the controller's Z after M6 is unknown to
                // the file; the first rapid may therefore only be judged on where it ends.
                var first = i == 0;
                if (s.XyLen > 0.01 && !first && lowZ < safeZ - 1e-6)
                    violations.Add($"rapid XY below safe Z {Fmt(safeZ)}: {where}");
                if (first && s.XyLen > 0.01 && s.Z1 < safeZ - 1e-6)
                    violations.Add($"first rapid moves XY without reaching safe Z {Fmt(safeZ)}: {where}");
                if (s.Z1 < materialTopZ + SpoilboardAllowanceMm - 1e-6)
                    violations.Add($"rapid ends inside the material zone (top {Fmt(materialTopZ)}): {where}");
                continue;
            }

            if (lowZ < deepestAllowedZ - 1e-6)
                violations.Add($"cut deeper than spoilboard allowance ({Fmt(deepestAllowedZ)}): {where}");
            if (s.Feed <= 0)
                violations.Add($"cut without feed: {where}");
            else if (!allowedFeeds.Contains(s.Feed))
                violations.Add($"feed F{Fmt(s.Feed)} is not a {source} value: {where}");
            if (s.Rpm <= 0)
                violations.Add($"cut before spindle on: {where}");
            else if (!allowedRpm.Contains(s.Rpm))
                violations.Add($"S{Fmt(s.Rpm)} is not a {source} value: {where}");

            var plunging = s.Z1 < s.Z0 - 1e-6 && s.Z1 < materialTopZ + 1e-6;
            if (plunging && s.XyLen > 0.01 && !s.Arc)
                violations.Add($"ramped plunge (XY motion while descending): {where}");
            if (plunging && !plungeFeeds.Contains(s.Feed))
                violations.Add($"plunge at cutting feed F{Fmt(s.Feed)}: {where}");

            var radius = RadiusOf(troy ? "T" + s.ToolNum : toolId, tools);
            var slack = 2 * radius + OutsidePanelSlackMm;
            if (!placed.Any(r => r.Contains(s.X0, s.Y0, slack) && r.Contains(s.X1, s.Y1, slack)))
                violations.Add($"cut outside every placed panel (+{Fmt(slack)}): {where}");
        }

        violations.AddRange(FrameViolations(nc, replay, troy));

        var last = replay.Strokes[^1];
        if (last.Z1 < safeZ - 1e-6)
            violations.Add($"program ends with the tool below safe Z: Z={Fmt(last.Z1)}");
        return violations;
    }

    /// <summary>Program frame: spindle on before any cut after each tool change; spindle off and end-of-program present.</summary>
    static IEnumerable<string> FrameViolations(string nc, OsaiReplay replay, bool troy)
    {
        var spindleOn = false;
        var sawEnd = false;
        var sawStop = false;
        foreach (var line in replay.Lines)
        {
            if (line.IsComment || line.Words.Count == 0) continue;
            var g = line.Words.Where(w => w.Letter == 'G').Select(w => (double?)w.Number).FirstOrDefault();
            var m = line.Words.Where(w => w.Letter == 'M').Select(w => (double?)w.Number).FirstOrDefault();
            var hasXyz = line.Words.Any(w => w.Letter is 'X' or 'Y' or 'Z');
            if (m is 3) spindleOn = true;
            if (m is 5) { spindleOn = false; sawStop = true; }
            if (m is 6) spindleOn = false;
            if (m is 2 or 30) sawEnd = true;
            if (g is 1 or 2 or 3 && hasXyz && !spindleOn)
                yield return $"cutting move with spindle off (line: {string.Join(' ', line.Words.Select(w => w.Letter + Fmt(w.Number)))})";
        }
        if (!sawEnd) yield return "no end of program (M30/M2)";
        if (!sawStop) yield return "spindle never stopped (M5)";
        if (!troy && nc.Contains("M6", StringComparison.Ordinal))
            yield return "Sheet×Tool program contains M6 — one tool per file is the contract";
    }

    // --- fixtures ----------------------------------------------------------------------

    /// <summary>Three panels: window + groove + hole, pocket, and a small part whose sides fall in the ramp range.</summary>
    static GoldenJob ShopMix(string post)
    {
        var baseA = GoldenFixtures.Rect("A", "oak", 18, 300, 200, hole: true, groove: true);
        var a = Panel("A", 300, 200,
        [
            .. baseA.Features,
            new PanelFeature
            {
                FeatureId = "CUT-1", Kind = "cutout", FaceId = "THROUGH", Through = true,
                Path = [new(120, 60), new(160, 60), new(160, 110), new(120, 110)],
            },
        ]);
        var b = Panel("B", 250, 180,
        [
            new PanelFeature
            {
                FeatureId = "PK1", Kind = "pocket", FaceId = "A", DepthMm = 6,
                Path = [new(60, 50), new(160, 50), new(160, 120), new(60, 120)],
            },
        ]);
        var c = GoldenFixtures.Rect("C", "oak", 18, 120, 70, hole: true, groove: false);
        return new GoldenJob { Id = "shop_mix_" + post, Post = post, Panels = [a, b, c] };
    }

    static Panel Panel(string id, double w, double h, IReadOnlyList<PanelFeature> features) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
        Features = features,
        Identity = new WorkpieceIdentity { WorkpieceId = id, ModuleId = "GOLD", ProjectId = "REG" },
    };

    /// <summary>Normalised (no N-numbers, LF) so mutation anchors read like the golden file.</summary>
    static (GoldenJob Job, GoldenPipeline Pipeline, string Nc) TroyProgram()
    {
        var job = GoldenFixtures.TroySingleFileAtc();
        var p = GoldenJobRunner.Prepare(job);
        var nc = NcTextNormalizer.Normalize(GoldenJobRunner.EmitPrograms(job, p).Single().NcText);
        return (job, p, nc);
    }

    static string ReplaceOnce(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(i >= 0, $"mutation anchor not found in program: {oldValue.Replace("\n", "\\n")}");
        return text[..i] + newValue + text[(i + oldValue.Length)..];
    }

    static double RadiusOf(string? toolId, IReadOnlyDictionary<string, ToolDefinition> tools) =>
        toolId is not null && tools.TryGetValue(toolId, out var t) && t.DiameterMm > 0
            ? t.DiameterMm * 0.5
            : TroyRecipe.WorkDiameterMm * 0.5;

    static List<Rect> PlacedBounds(GoldenJob job, NestResult nest)
    {
        var byId = job.Panels.ToDictionary(x => x.PanelId, StringComparer.Ordinal);
        var rects = new List<Rect>();
        foreach (var pl in nest.Placements)
        {
            var panel = byId[pl.PanelId];
            var pts = panel.Outline.Points;
            var w = pts.Max(q => q.X) - pts.Min(q => q.X);
            var h = pts.Max(q => q.Y) - pts.Min(q => q.Y);
            var quarter = Math.Abs(((pl.RotationDeg % 180) + 180) % 180 - 90) < 1e-6;
            if (quarter) (w, h) = (h, w);
            rects.Add(new Rect(pl.OffsetX, pl.OffsetY, pl.OffsetX + w, pl.OffsetY + h));
        }
        return rects;
    }

    readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
    {
        public bool Contains(double x, double y, double slack) =>
            x >= MinX - slack && x <= MaxX + slack && y >= MinY - slack && y <= MaxY + slack;
    }

    static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
