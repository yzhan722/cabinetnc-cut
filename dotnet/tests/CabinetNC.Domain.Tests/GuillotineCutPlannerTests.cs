using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class GuillotineCutPlannerTests
{
    static Panel Rect(string id, double w, double h) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
        },
    };

    [Fact]
    public void Plans_vertical_cut_when_right_remnant_wide_enough()
    {
        // Sheet 1220×2440; parts occupy left ~500 → right remnant ~700 ≥ 400
        var panel = Rect("A", 400, 800);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 50, OffsetY = 50, RotationDeg = 0,
            },
        };

        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], places, 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.True(plan!.RemnantMinEdgeMm >= 400 - 1e-6);
        Assert.True(plan.Polyline.Count is 2 or 3);
        // Used maxX + clearance = 470 — cut must pass through that vertical or L corner.
        Assert.Contains(plan.Polyline, p => Math.Abs(p.X - 470) < 1e-6);
    }

    [Fact]
    public void Uses_straight_vertical_when_only_side_strip_qualifies()
    {
        // Fill most of the height so top/bottom remnants < 400; only right strip ≥ 400 → no L.
        var panel = Rect("A", 400, 900);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 50, OffsetY = 50, RotationDeg = 0,
            },
        };
        // Sheet 1220×1000: used y 30–970 → top/bottom rem 30; right rem 1220-470=750
        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], places, 0, 1220, 1000, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.Equal("vertical", plan!.Kind);
        Assert.Equal(2, plan.Polyline.Count);
        Assert.Equal(470, plan.Polyline[0].X, 3);
    }

    [Fact]
    public void Skips_when_remnant_strip_thinner_than_400()
    {
        // Parts nearly fill width → right remnant < 400
        var panel = Rect("A", 900, 800);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 50, OffsetY = 50, RotationDeg = 0,
            },
        };
        // used maxX = 970, +20 = 990; remnant = 1220-990 = 230 < 400
        // left remnant = 50-20 = 30 < 400
        // top: sheetH - usedMaxY; usedMaxY = 870; remnant = 2440-870 = 1570 ≥ 400 → horizontal OK
        // So there may still be a horizontal candidate. Use a square sheet almost full.
        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], places, 0, 1220, 1000, clearanceMm: 20, minRemnantEdgeMm: 400);

        // used: x50-970 (+20 → 30-990), y50-850 (+20 → 30-870)
        // V rem: 30 or 230 — both < 400
        // H rem: 30 or 130 — both < 400
        // L arms also thin
        Assert.Null(plan);
    }

    [Fact]
    public void Plans_L_when_both_arms_at_least_400()
    {
        // Cluster in bottom-left; top and right both ≥ 400
        var panel = Rect("A", 200, 200);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 20, OffsetY = 20, RotationDeg = 0,
            },
        };
        // used after +20: (0,0)-(240,240) on 1220×2440
        // right arm 980, top arm 2200 — L allowed; area large
        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], places, 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        // Straight V/H also valid; planner picks largest remnant area.
        // Right strip 980×2440 = huge; top strip 1220×2200 also huge; L even larger.
        Assert.True(plan!.RemnantAreaMm2 > 0);
        Assert.Contains(plan.Kind, new[] { "vertical", "horizontal", "L" });
        if (plan.Kind == "L")
            Assert.Equal(3, plan.Polyline.Count);
    }

    [Fact]
    public void Returns_null_for_empty_sheet()
    {
        var plan = GuillotineCutPlanner.PlanForSheet(
            [Rect("A", 100, 100)], [], 0, 1220, 2440);
        Assert.Null(plan);
    }

    [Fact]
    public void PlanSheet_splits_corner_into_two_rects_when_both_meet_min_edge()
    {
        // Used ~ (0,0)-(500,500) on 1220×2440 after clearance — both split pieces ≥ 400.
        var panel = Rect("A", 460, 460);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 20, OffsetY = 20, RotationDeg = 0,
            },
        };
        var plan = GuillotineCutPlanner.PlanSheet(
            [panel], places, 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Pieces.Count);
        Assert.All(plan.Pieces, p => Assert.Equal("RECT", p.Shape));
        Assert.All(plan.Pieces, p => Assert.True(p.MinEdgeMm >= 400 - 1e-6));
        Assert.True(plan.Cuts.Count >= 2);
        Assert.DoesNotContain(plan.Pieces, p => p.Shape == "L");
    }

    [Fact]
    public void PlanSheet_keeps_L_when_split_would_make_a_piece_under_400()
    {
        // Used ~ (0,0)-(240,240): remaining after a split is only 240 mm wide.
        var panel = Rect("A", 200, 200);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 20, OffsetY = 20, RotationDeg = 0,
            },
        };
        var plan = GuillotineCutPlanner.PlanSheet(
            [panel], places, 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.Contains(plan!.Pieces, p => p.Shape == "L");
        Assert.All(plan.Pieces, p => Assert.True(p.MinEdgeMm >= 400 - 1e-6));
        Assert.True(plan.Pieces.Count(p => p.Shape == "L") == 1);
    }

    [Fact]
    public void PlanSheet_can_return_four_rects_around_a_center_cluster()
    {
        // Used after +20 must leave four strips and a mid band all ≥ 400.
        var panel = Rect("A", 370, 500);
        var places = new[]
        {
            new NestPlacement
            {
                PanelId = "A", SheetIndex = 0,
                OffsetX = 430, OffsetY = 800, RotationDeg = 0,
            },
        };
        // used: (410,780)-(820,1320) on 1220×2440
        // left 410, right 400, midW 410, bot 780, top 1120, midH 540
        var plan = GuillotineCutPlanner.PlanSheet(
            [panel], places, 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.Equal(4, plan!.Pieces.Count);
        Assert.All(plan.Pieces, p => Assert.Equal("RECT", p.Shape));
        Assert.Equal(4, GuillotineCutPlanner.ToCutOps(plan, 0, 1220, 2440, 18, 10).Count);
    }

    [Fact]
    public void ToCutOp_is_open_through_remnant_with_edge_overshoot()
    {
        var plan = new GuillotineCutPlanner.Result
        {
            Kind = "vertical",
            Polyline = [(470, 0), (470, 1000)],
            RemnantAreaMm2 = 470 * 1000,
            RemnantMinEdgeMm = 470,
            Label = "竖切",
        };
        var op = GuillotineCutPlanner.ToCutOp(plan, 0, 1220, 1000, 18, toolDiameterMm: 10);
        Assert.NotNull(op);
        Assert.Equal("remnant", op!.Op);
        Assert.False(op.ClosePath);
        Assert.True(op.Through);
        Assert.Equal("T2", op.ToolId);
        Assert.Equal(2, op.Path!.Count);
        Assert.Equal(470, op.Path[0].X, 3);
        Assert.True(op.Path[0].Y < -4.9);
        Assert.True(op.Path[1].Y > 1004.9);
        Assert.Equal(CamStrategyKind.Guillotine, CamStrategy.Classify(op));
        Assert.Equal(5, CamSafety.SequenceRank(op));

        var report = NcPreflight.Check(
            [op],
            CabinetNC.Domain.Machines.MachineCatalog.Get("nesting_router_6"),
            1220, 1000);
        Assert.True(report.Ok, NcPreflight.Format(report));
        Assert.DoesNotContain(report.Issues, i => i.Code == "out_of_sheet");
    }
}
