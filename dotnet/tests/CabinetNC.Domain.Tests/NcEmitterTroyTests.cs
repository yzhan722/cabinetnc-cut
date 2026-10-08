using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Domain.Tests;

public class NcEmitterTroyTests
{
    static MachineProfile Machine() => MachineCatalog.Get("nesting_router_6");

    static string Troy(params CutOp[] ops) =>
        NcEmitter.OpsToNc(ops, Machine(), recipe: PostRecipe.TroyDefault());

    static CutOp Drill(string panel = "P1", bool through = true, double depth = 18, double th = 18) => new()
    {
        Op = "drill",
        PanelId = panel,
        FeatureId = "H1",
        ToolId = "T3",
        Placed = true,
        SheetX = 30,
        SheetY = 40,
        DiameterMm = 3,
        DepthMm = depth,
        ThicknessMm = th,
        Through = through,
    };

    static CutOp Tongue() => new()
    {
        Op = "groove",
        PanelId = "P1",
        FeatureId = "TG1",
        ToolId = "T1",
        Placed = true,
        IsTongue = true,
        DepthMm = 9,
        ThicknessMm = 18,
        Path = [(10, 10), (190, 10)],
        ClosePath = false,
    };

    static CutOp Pocket() => new()
    {
        Op = "pocket",
        PanelId = "P1",
        FeatureId = "PK1",
        ToolId = "T2",
        Placed = true,
        DepthMm = 12,
        ThicknessMm = 18,
        ClosePath = false,
        PathSegments = [new (double X, double Y)[] { (20, 20), (80, 20) }],
        FinishLoop = [(22, 22), (78, 22), (78, 48), (22, 48), (22, 22)],
    };

    static CutOp Outer(string panel = "P1") => new()
    {
        Op = "contour",
        PanelId = panel,
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Through = true,
        ThicknessMm = 18,
        DepthMm = 18.5,
        Path = [(0, 0), (200, 0), (200, 100), (0, 100)],
    };

    static CutOp Inner() => new()
    {
        Op = "contour",
        PanelId = "P1",
        FeatureId = "W1",
        ToolId = "T2",
        Placed = true,
        ClosePath = true,
        Through = true,
        ThicknessMm = 18,
        DepthMm = 18.5,
        Path = [(40, 30), (80, 30), (80, 70), (40, 70)],
    };

    static string[] Lines(string nc) => nc.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void Matches_OSAI_Troy_header_and_end()
    {
        var nc = Troy(Outer());
        var lines = Lines(nc);
        Assert.Equal("N1 G90 ", lines[0]);
        Assert.Equal("N2 G40 ", lines[1]);
        Assert.Equal("N3 G80 ", lines[2]);
        Assert.Equal("N4 (UAO,1)", lines[3]);
        Assert.Equal("N5 G79 Z0", lines[4]);
        Assert.Equal("N6 M05", lines[5]);
        Assert.Equal("N7 M52", lines[6]);
        Assert.Equal("N8 M6 T2", lines[7]);
        Assert.Equal("N9 M3 S14500", lines[8]);
        Assert.Equal("N10 (DLY,3)", lines[9]);
        Assert.Equal("N11 M49", lines[10]);
        Assert.Equal("N12 G27", lines[11]);
        Assert.Equal("N13 G17", lines[12]);
        Assert.Equal("N14 G0 X0.0000 Y0.0000", lines[13]);
        Assert.Contains("G0 Z30.0000", nc);
        Assert.DoesNotContain("G21", nc);
        Assert.DoesNotContain("S14500 M3", nc);
        var body = lines.Where(l => l.Length > 0).ToList();
        Assert.Contains("G0 X0.0000 Y0.0000", body[^5]);
        Assert.EndsWith(" G80", body[^4]);
        Assert.EndsWith(" M5", body[^3]);
        Assert.EndsWith(" G79 Z0", body[^2]);
        Assert.EndsWith(" M30", body[^1]);
        Assert.DoesNotContain("\nM2\n", "\n" + nc.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Home_xy_at_end_can_be_turned_off()
    {
        var nc = NcEmitter.OpsToNc([Outer()], Machine(), recipe: new PostRecipe { HomeXyAtEnd = false });
        var body = Lines(nc).Where(l => l.Length > 0).ToList();
        Assert.EndsWith(" G80", body[^4]);
        Assert.DoesNotContain("G0 X0.0000 Y0.0000", body[^5]);
    }

    [Fact]
    public void Drill_then_tongue_then_clearance_then_profile_with_toolchange()
    {
        var nc = Troy(Outer(), Pocket(), Drill(), Tongue(), Inner());
        var t3 = nc.IndexOf("M6 T3", StringComparison.Ordinal);
        var t1 = nc.IndexOf("M6 T1", StringComparison.Ordinal);
        var t2 = nc.IndexOf("M6 T2", StringComparison.Ordinal);
        Assert.True(t3 >= 0 && t1 >= 0 && t2 >= 0);
        Assert.True(t3 < t1);
        Assert.True(t1 < t2);
        Assert.Contains("Z0.5000", nc);
        Assert.Contains("Z-0.5500", nc);
        var f12 = nc.IndexOf("F12000.0", StringComparison.Ordinal);
        var f20 = nc.IndexOf("F20000.0", StringComparison.Ordinal);
        Assert.True(f12 >= 0 && f20 > f12);
    }

    [Fact]
    public void Inner_and_outer_share_leave_pass_then_share_through_pass()
    {
        var nc = Troy(Outer(), Inner());
        var leaves = new List<int>();
        var throughs = new List<int>();
        var lines = Lines(nc);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("G1 Z0.5000", StringComparison.Ordinal))
                leaves.Add(i);
            if (lines[i].Contains("G1 Z-0.5500", StringComparison.Ordinal))
                throughs.Add(i);
        }
        Assert.Equal(2, leaves.Count);
        Assert.Equal(2, throughs.Count);
        Assert.True(leaves[0] < leaves[1]);
        Assert.True(leaves[1] < throughs[0], nc);
        Assert.True(throughs[0] < throughs[1]);
    }

    [Fact]
    public void Tool_change_does_not_home_xy()
    {
        var nc = Troy(Outer(), Drill(), Tongue());
        var lines = Lines(nc);
        var homes = lines
            .Select((l, i) => (l, i))
            .Where(t => t.l.Contains("G0 X0.0000 Y0.0000", StringComparison.Ordinal))
            .Select(t => t.i)
            .ToList();
        Assert.Equal(2, homes.Count);
        Assert.True(homes[0] < 20, nc);
        Assert.True(homes[1] >= lines.Length - 6, nc);
    }

    [Fact]
    public void Rebate_ring_one_plunge_stays_down_between_walls()
    {
        var panel = new CabinetNC.Domain.Parts.Panel
        {
            PanelId = "LID",
            ThicknessMm = 18,
            Outline = new CabinetNC.Domain.Geometry.Outline
            {
                Points = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
            },
            Features =
            [
                new CabinetNC.Domain.Parts.PanelFeature
                {
                    FeatureId = "REBATE",
                    Kind = "pocket",
                    DepthMm = 9,
                    Path = [new(0, 0), new(447, 0), new(447, 277), new(0, 277)],
                    Holes =
                    [
                        [new(9, 9), new(438, 9), new(438, 268), new(9, 268)],
                    ],
                },
            ],
        };
        var ops = CabinetNC.Domain.Manufacturing.OpsPlanner.AttachToNest(
            CabinetNC.Domain.Manufacturing.OpsPlanner.FeaturesToOps([panel]),
            [new CabinetNC.Domain.Nesting.NestPlacement { PanelId = "LID", SheetIndex = 0 }]);
        var nc = Troy(ops.ToArray());
        var beforeProfile = nc.Split("M6 T2")[0];
        Assert.Equal(1, Lines(beforeProfile).Count(l => l.Contains("G1 Z9.0000", StringComparison.Ordinal)));
        Assert.Equal(2, Lines(beforeProfile).Count(l => l.Contains("G0 Z30.0000", StringComparison.Ordinal)));
        Assert.DoesNotContain("Z-0.5500", beforeProfile);
    }

    [Fact]
    public void Shop_feeds_and_board_bottom_z()
    {
        var nc = Troy(Outer(), Tongue(), Pocket(), Drill());
        Assert.Contains("F1000.0", nc);
        Assert.Contains("F9000.0", nc);
        Assert.Contains("F12000.0", nc);
        Assert.Contains("F20000.0", nc);
        Assert.Contains("G1 Z9.0000 F1000.0", nc);
        Assert.Contains("G1 X190.0000 F9000.0", nc);
        Assert.Contains("G1 Z6.0000 F1000.0", nc);
    }

    [Fact]
    public void Groove_stays_at_cut_depth_when_finish_starts_at_spiral_end()
    {
        var groove = Tongue() with
        {
            Path = [(10, 10), (20, 10)],
            PathSegments =
            [
                new (double X, double Y)[] { (10, 10), (20, 10) },
            ],
            FinishLoop = [(20, 10), (20, 20), (10, 20), (10, 10), (20, 10)],
        };

        var nc = Troy(groove);

        Assert.True(
            Lines(nc).Count(l => l.Contains("G0 Z30.0000", StringComparison.Ordinal)) == 2,
            nc);
        Assert.Equal(1, Lines(nc).Count(l => l.Contains("G1 Z9.0000", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pocket_stays_at_cut_depth_when_finish_starts_at_spiral_end()
    {
        var pocket = Pocket() with
        {
            PathSegments =
            [
                new (double X, double Y)[] { (10, 10), (20, 10) },
            ],
            FinishLoop = [(20, 10), (20, 20), (10, 20), (10, 10), (20, 10)],
        };

        var nc = Troy(pocket);

        Assert.Equal(2, Lines(nc).Count(l => l.Contains("G0 Z30.0000", StringComparison.Ordinal)));
        Assert.Equal(1, Lines(nc).Count(l => l.Contains("G1 Z6.0000", StringComparison.Ordinal)));
    }

    [Fact]
    public void Blind_drill_stops_above_bottom_through_drill_overshoots()
    {
        var blind = Troy(Drill(through: false, depth: 12, th: 18));
        Assert.Contains("G1 Z6.0000 F1000.0", blind);
        Assert.DoesNotContain("Z-0.5500", blind);

        var through = Troy(Drill(through: true));
        Assert.Contains("G1 Z-0.5500 F1000.0", through);
    }

    [Fact]
    public void Last_pass_follows_same_xy_over_bridges_at_leave_z()
    {
        var recipe = new PostRecipe
        {
            Bridges =
            [
                new ProfileBridge
                {
                    Id = "b1",
                    PanelId = "P1",
                    SheetIndex = 0,
                    ArcLengthMm = 100,
                    X = 100,
                    Y = 0,
                    WidthMm = 10,
                },
            ],
        };
        var nc = NcEmitter.OpsToNc([Outer()], Machine(), recipe: recipe);
        Assert.Contains("Z-0.5500", nc);
        Assert.Contains("F20000.0", nc);
        var last = nc[nc.IndexOf("Z-0.5500", StringComparison.Ordinal)..];
        Assert.DoesNotContain("G0 X110.0000", last);
        Assert.Contains("G1 X110.0000", last);
        Assert.Contains("G1 Z0.5000 F1000.0", last);
        Assert.DoesNotContain("G1 Z1.4500", last);
    }

    [Fact]
    public void First_pass_cuts_through_bridges_at_leave_z()
    {
        var recipe = new PostRecipe
        {
            Bridges =
            [
                new ProfileBridge
                {
                    Id = "b1",
                    PanelId = "P1",
                    SheetIndex = 0,
                    ArcLengthMm = 100,
                    X = 100,
                    Y = 0,
                    WidthMm = 10,
                },
            ],
        };
        var nc = NcEmitter.OpsToNc([Outer()], Machine(), recipe: recipe);
        var first = nc[..nc.IndexOf("Z-0.5500", StringComparison.Ordinal)];
        Assert.Contains("G1 Z0.5000", first);
        Assert.DoesNotContain("G1 Z1.4500", first);
        Assert.Equal(2, Lines(first).Count(l => l.Contains("G0 Z30.0000", StringComparison.Ordinal)));
    }

    [Fact]
    public void Bridge_web_adds_two_tool_radii_to_tool_centre_skip()
    {
        Assert.Equal(15, ProfileBridgePlanner.ToolCenterSpanMm(5, 10));
        var recipe = new PostRecipe
        {
            Bridges =
            [
                new ProfileBridge
                {
                    Id = "b1",
                    PanelId = "P1",
                    SheetIndex = 0,
                    ArcLengthMm = 100,
                    X = 100,
                    Y = 0,
                    WidthMm = 5,
                },
            ],
        };
        var nc = NcEmitter.OpsToNc([Outer()], Machine(), recipe: recipe);
        Assert.Contains("G1 X92.5000", nc);
        Assert.Contains("G1 X107.5000", nc);
        var last = nc[nc.IndexOf("Z-0.5500", StringComparison.Ordinal)..];
        Assert.Contains("G1 Z0.5000 F1000.0", last);
    }

    [Fact]
    public void Paired_bridges_are_emitted_on_both_panel_profiles()
    {
        var a = Outer("A");
        var b = Outer("B") with
        {
            Path = [(220, 0), (420, 0), (420, 100), (220, 100)],
        };
        var recipe = new PostRecipe
        {
            Bridges =
            [
                new ProfileBridge
                {
                    Id = "a",
                    PairId = "b",
                    PanelId = "A",
                    SheetIndex = 0,
                    ArcLengthMm = 100,
                    X = 100,
                    Y = 0,
                    WidthMm = 10,
                },
                new ProfileBridge
                {
                    Id = "b",
                    PairId = "a",
                    PanelId = "B",
                    SheetIndex = 0,
                    ArcLengthMm = 100,
                    X = 320,
                    Y = 0,
                    WidthMm = 10,
                },
            ],
        };

        var nc = NcEmitter.OpsToNc([a, b], Machine(), recipe: recipe);

        // First pass: one plunge per panel. Last pass: leave each pair at 0.5.
        Assert.Equal(2, Lines(nc[..nc.IndexOf("Z-0.5500", StringComparison.Ordinal)])
            .Count(l => l.Contains("G1 Z0.5000 F1000.0", StringComparison.Ordinal)));
        Assert.Equal(2, Lines(nc[nc.IndexOf("Z-0.5500", StringComparison.Ordinal)..])
            .Count(l => l.Contains("G1 Z0.5000 F1000.0", StringComparison.Ordinal)));
        Assert.DoesNotContain("G1 Z1.4500", nc);
    }

    [Fact]
    public void Last_pass_adapts_unpaired_facing_bridge_onto_neighbor()
    {
        var a = Outer("A") with
        {
            Path = [(0, 0), (100, 0), (100, 50), (0, 50)],
        };
        var b = Outer("B") with
        {
            Path = [(112, 0), (212, 0), (212, 50), (112, 50)],
        };
        var recipe = new PostRecipe
        {
            Bridges =
            [
                new ProfileBridge
                {
                    Id = "a",
                    PanelId = "A",
                    SheetIndex = 0,
                    ArcLengthMm = 125,
                    X = 100,
                    Y = 25,
                    WidthMm = 5,
                },
            ],
        };
        var nc = NcEmitter.OpsToNc([a, b], Machine(), recipe: recipe);
        var last = nc[nc.IndexOf("Z-0.5500", StringComparison.Ordinal)..];
        Assert.Equal(2, Lines(last).Count(l =>
            l.Contains("G1 Z0.5000 F1000.0", StringComparison.Ordinal)));
    }

    [Fact]
    public void First_pass_outers_nearest_neighbor_not_panel_id()
    {
        var far = Outer("A") with
        {
            Path = [(800, 0), (880, 0), (880, 60), (800, 60)],
        };
        var near = Outer("Z") with
        {
            Path = [(20, 0), (100, 0), (100, 60), (20, 60)],
        };
        var nc = NcEmitter.OpsToNc([far, near], Machine(), recipe: PostRecipe.TroyDefault());
        var first = nc[..nc.IndexOf("Z-0.5500", StringComparison.Ordinal)];
        var nearGo = first.IndexOf("G0 X20.0000", StringComparison.Ordinal);
        var farGo = first.IndexOf("G0 X800.0000", StringComparison.Ordinal);
        Assert.True(nearGo >= 0 && farGo >= 0, first);
        Assert.True(nearGo < farGo, first);
    }

    [Fact]
    public void First_pass_ramp_recuts_entry_at_leave_z()
    {
        var recipe = new PostRecipe { ProfileFirstRamp45 = true };
        var square = Outer() with
        {
            Path = [(0, 0), (100, 0), (100, 100), (0, 100)],
        };
        var nc = NcEmitter.OpsToNc([square], Machine(), recipe: recipe);
        Assert.Contains("G1 X29.5000 Z0.5000 F1000.0", nc);
        Assert.Contains("F12000.0", nc);
    }

    [Fact]
    public void Shop_sample_plan_to_nc_writes_inspectable_file()
    {
        var panel = new CabinetNC.Domain.Parts.Panel
        {
            PanelId = "A",
            ThicknessMm = 18,
            Outline = new CabinetNC.Domain.Geometry.Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 800), new(0, 800)],
            },
        };
        var places = new[]
        {
            new CabinetNC.Domain.Nesting.NestPlacement
            {
                PanelId = "A", SheetIndex = 0, OffsetX = 50, OffsetY = 50,
            },
        };
        var plan = CabinetNC.Domain.Nesting.GuillotineCutPlanner.PlanForSheet(
            [panel], places, 0, 1220, 2440, 20, 400);
        Assert.NotNull(plan);
        var remnant = CabinetNC.Domain.Nesting.GuillotineCutPlanner.ToCutOp(
            plan!, 0, 1220, 2440, 18, 10);
        Assert.NotNull(remnant);
        var nc = NcEmitter.OpsToNc([Outer(), remnant!], Machine(), recipe: PostRecipe.TroyDefault());
        var path = Path.Combine(Path.GetTempPath(), "omnicam-guillotine-sample.anc");
        File.WriteAllText(path, nc);
        Assert.Contains("F20000.0", nc);
        Assert.Contains("F9000.0", nc);
        Assert.Contains("Z-0.5500", nc);
        var f20 = nc.LastIndexOf("F20000.0", StringComparison.Ordinal);
        var f9 = nc.LastIndexOf("F9000.0", StringComparison.Ordinal);
        Assert.True(f9 > f20, "remnant feed must follow profile last");
        var body = Lines(nc).Where(l => l.Length > 0).ToList();
        Assert.Contains("G0 X0.0000 Y0.0000", body[^5]);
    }

    [Fact]
    public void Guillotine_cut_follows_profile_last_then_homes_xy()
    {
        var remnant = new CutOp
        {
            Op = "remnant",
            PanelId = "SHEET-0-REMNANT",
            FeatureId = "guillotine",
            ToolId = "T2",
            Placed = true,
            ClosePath = false,
            Through = true,
            ThicknessMm = 18,
            DepthMm = 18.5,
            Path = [(470, -5), (470, 1005)],
        };
        var recipe = new PostRecipe
        {
            GuillotineFeed = 9000,
            GuillotinePlunge = 1000,
            GuillotineThroughZMm = -0.55,
            HomeXyAtEnd = true,
        };
        var nc = NcEmitter.OpsToNc([Outer(), remnant], Machine(), recipe: recipe);
        var f20 = nc.LastIndexOf("F20000.0", StringComparison.Ordinal);
        var cutX = nc.LastIndexOf("X470.0000", StringComparison.Ordinal);
        var plunge = nc.IndexOf("G1 Z-0.5500 F1000.0", StringComparison.Ordinal);
        Assert.True(f20 >= 0 && cutX > f20);
        Assert.True(plunge >= 0);
        Assert.Contains("F9000.0", nc);
        var body = Lines(nc).Where(l => l.Length > 0).ToList();
        Assert.Contains("G0 X0.0000 Y0.0000", body[^5]);
        Assert.EndsWith(" G80", body[^4]);
        Assert.EndsWith(" M30", body[^1]);
    }

    [Fact]
    public void Legacy_path_unchanged_without_recipe()
    {
        var nc = NcEmitter.OpsToNc(
            [Outer() with { Path = [(20, 20), (30, 20), (30, 30), (20, 30)] }],
            Machine());
        Assert.Contains("depth=18.5", nc);
        Assert.DoesNotContain("(UAO,1)", nc);
        Assert.DoesNotContain("G79 Z0", nc);
    }
}
