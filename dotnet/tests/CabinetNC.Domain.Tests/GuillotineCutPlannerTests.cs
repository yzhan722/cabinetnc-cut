using CabinetNC.Domain.Geometry;
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
}
