using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestP0InteractionTests
{
    [Theory]
    [InlineData(-100, 50, 10, 50)]
    [InlineData(50, -100, 50, 10)]
    [InlineData(500, 50, 290, 50)]
    [InlineData(50, 500, 50, 190)]
    public void Clamp_on_sheet_respects_all_borders(
        double requestedX,
        double requestedY,
        double expectedX,
        double expectedY)
    {
        var result = NestDrag.ClampOnSheet(
            Rect("A", 100, 100),
            requestedX,
            requestedY,
            rotDeg: 0,
            sheetW: 400,
            sheetH: 300,
            borderMm: 10);

        Assert.Equal(expectedX, result.Ox, 6);
        Assert.Equal(expectedY, result.Oy, 6);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void Quarter_turn_swaps_dimensions_when_clamping(double rotation)
    {
        var result = NestDrag.ClampOnSheet(
            Rect("A", 160, 80),
            ox: 500,
            oy: 500,
            rotDeg: rotation,
            sheetW: 300,
            sheetH: 300,
            borderMm: 10);

        Assert.Equal(210, result.Ox, 6);
        Assert.Equal(130, result.Oy, 6);
    }

    [Fact]
    public void Drag_overlap_returns_exact_fallback_and_blocked()
    {
        var moving = Rect("A", 100, 100);
        var other = Rect("B", 100, 100);
        var result = NestDrag.Resolve(
            moving,
            "A",
            ox: 50,
            oy: 50,
            rotDeg: 0,
            sheetIndex: 0,
            others: [("B", 0, 80, 80, 0)],
            byId: new Dictionary<string, Panel> { ["A"] = moving, ["B"] = other },
            sheetW: 500,
            sheetH: 500,
            spacingMm: 8,
            borderMm: 10,
            fallback: (15, 25),
            allowOverlap: false);

        Assert.True(result.Blocked);
        Assert.Equal(15, result.Ox);
        Assert.Equal(25, result.Oy);
    }

    [Fact]
    public void Other_sheet_does_not_block_drag()
    {
        var moving = Rect("A", 100, 100);
        var other = Rect("B", 100, 100);
        var result = NestDrag.Resolve(
            moving,
            "A",
            50,
            50,
            0,
            sheetIndex: 0,
            others: [("B", 1, 50, 50, 0)],
            byId: new Dictionary<string, Panel> { ["A"] = moving, ["B"] = other },
            sheetW: 500,
            sheetH: 500,
            spacingMm: 8,
            borderMm: 10,
            fallback: (10, 10),
            allowOverlap: false);

        Assert.False(result.Blocked);
        Assert.Equal(50, result.Ox);
        Assert.Equal(50, result.Oy);
    }

    [Fact]
    public void Same_panel_id_does_not_block_itself()
    {
        var moving = Rect("A", 100, 100);
        var result = NestDrag.Resolve(
            moving,
            "A",
            50,
            50,
            0,
            sheetIndex: 0,
            others: [("A", 0, 50, 50, 0)],
            byId: new Dictionary<string, Panel> { ["A"] = moving },
            sheetW: 500,
            sheetH: 500,
            spacingMm: 8,
            borderMm: 10,
            fallback: (10, 10),
            allowOverlap: false);

        Assert.False(result.Blocked);
    }

    [Theory]
    [InlineData(108.0, false)]
    [InlineData(107.999, true)]
    public void Drag_clearance_boundary_is_deterministic(double otherX, bool expectedBlocked)
    {
        var moving = Rect("A", 100, 100);
        var other = Rect("B", 100, 100);
        var result = NestDrag.Resolve(
            moving,
            "A",
            0,
            0,
            0,
            sheetIndex: 0,
            others: [("B", 0, otherX, 0, 0)],
            byId: new Dictionary<string, Panel> { ["A"] = moving, ["B"] = other },
            sheetW: 500,
            sheetH: 500,
            spacingMm: 8,
            borderMm: 0,
            fallback: (20, 20),
            allowOverlap: false);

        Assert.Equal(expectedBlocked, result.Blocked);
    }

    [Fact]
    public void Allow_overlap_still_clamps_to_sheet()
    {
        var moving = Rect("A", 100, 100);
        var result = NestDrag.Resolve(
            moving,
            "A",
            -50,
            999,
            0,
            sheetIndex: 0,
            others: [],
            byId: new Dictionary<string, Panel> { ["A"] = moving },
            sheetW: 400,
            sheetH: 300,
            spacingMm: 8,
            borderMm: 10,
            fallback: (20, 20),
            allowOverlap: true);

        Assert.False(result.Blocked);
        Assert.Equal(10, result.Ox);
        Assert.Equal(190, result.Oy);
    }

    [Theory]
    [InlineData(14.9, 10, 10)]
    [InlineData(15.1, 10, 20)]
    [InlineData(-14.9, 10, -10)]
    [InlineData(-15.1, 10, -20)]
    [InlineData(12.4, 5, 10)]
    public void Snap_is_deterministic(double value, double step, double expected)
    {
        Assert.Equal(expected, NestDrag.SnapMm(value, step), 6);
    }

    [Fact]
    public void Gate_rejects_any_input_panel_missing_from_placements()
    {
        var panels = new[] { Rect("PLACED", 100, 100), Rect("HELD", 80, 60) };
        var placements = new[]
        {
            new NestPlacement
            {
                PanelId = "PLACED",
                SheetIndex = 0,
                OffsetX = 10,
                OffsetY = 10,
            },
        };

        var gate = NestExportGate.Check(panels, placements, 8);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("unplaced_panel:") && e.Contains("HELD"));
    }

    [Fact]
    public void Guillotine_preview_polyline_stays_inside_sheet()
    {
        var panel = Rect("A", 300, 200);
        var placement = new NestPlacement
        {
            PanelId = "A",
            SheetIndex = 0,
            OffsetX = 20,
            OffsetY = 20,
        };

        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], [placement], 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);

        Assert.NotNull(plan);
        Assert.All(plan!.Polyline, p =>
        {
            Assert.InRange(p.X, 0, 1220);
            Assert.InRange(p.Y, 0, 2440);
        });
    }

    [Theory]
    [InlineData(0, 2440)]
    [InlineData(1220, 0)]
    [InlineData(-1, 2440)]
    public void Guillotine_invalid_sheet_size_returns_null(double width, double height)
    {
        var plan = GuillotineCutPlanner.PlanForSheet(
            [Rect("A", 100, 100)],
            [new NestPlacement { PanelId = "A", SheetIndex = 0, OffsetX = 10, OffsetY = 10 }],
            0,
            width,
            height);

        Assert.Null(plan);
    }

    [Fact]
    public void Guillotine_preview_never_enters_nc()
    {
        var panel = Rect("A", 300, 200);
        var placement = new NestPlacement
        {
            PanelId = "A",
            SheetIndex = 0,
            OffsetX = 20,
            OffsetY = 20,
        };
        var plan = GuillotineCutPlanner.PlanForSheet(
            [panel], [placement], 0, 1220, 2440, clearanceMm: 20, minRemnantEdgeMm: 400);
        Assert.NotNull(plan);

        var op = new CutOp
        {
            Op = "contour",
            PanelId = "A",
            ToolId = "T1",
            Placed = true,
            SheetIndex = 0,
            DepthMm = 18.5,
            Path = [(20, 20), (320, 20), (320, 220), (20, 220)],
        };
        var nc = NcEmitter.OpsToNc([op], MachineCatalog.Get(MachineCatalog.DefaultId));

        Assert.DoesNotContain("guillotine", nc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan!.Label ?? "L切", nc, StringComparison.Ordinal);
    }

    static Panel Rect(string id, double w, double h) =>
        new()
        {
            PanelId = id,
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
                Closed = true,
            },
        };
}
