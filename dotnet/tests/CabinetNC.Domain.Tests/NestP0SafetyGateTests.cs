using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestP0SafetyGateTests
{
    [Fact]
    public void Gate_rejects_mixed_material_on_same_sheet()
    {
        var panels = new[]
        {
            Rect("A", "oak", 18),
            Rect("B", "mdf", 18),
        };
        var gate = NestExportGate.Check(panels, SeparatePlacements(), 8);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("mixed_group_sheet:"));
    }

    [Fact]
    public void Gate_rejects_mixed_thickness_on_same_sheet()
    {
        var panels = new[]
        {
            Rect("A", "oak", 18),
            Rect("B", "oak", 15),
        };
        var gate = NestExportGate.Check(panels, SeparatePlacements(), 8);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("mixed_group_sheet:"));
    }

    [Fact]
    public void Same_geometry_on_different_sheets_does_not_collide()
    {
        var panels = new[] { Rect("A"), Rect("B") };
        var placements = new[]
        {
            Place("A", sheet: 0, x: 10, y: 10),
            Place("B", sheet: 1, x: 10, y: 10),
        };

        var gate = NestExportGate.Check(panels, placements, 8);

        Assert.True(gate.Ok, string.Join("; ", gate.Errors));
    }

    [Theory]
    [InlineData(12.0, true)]
    [InlineData(8.0, true)]
    [InlineData(7.999, false)]
    [InlineData(0.0, false)]
    public void Clearance_boundary_is_deterministic(double actualGapMm, bool expectedOk)
    {
        var panels = new[] { Rect("A"), Rect("B") };
        var placements = new[]
        {
            Place("A", x: 0, y: 0),
            Place("B", x: 100 + actualGapMm, y: 0),
        };

        var gate = NestExportGate.Check(panels, placements, clearanceMm: 8);

        Assert.Equal(expectedOk, gate.Ok);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Empty_placements_respect_require_placements(bool requirePlacements, bool expectedOk)
    {
        var gate = NestExportGate.Check(
            [Rect("A")],
            [],
            clearanceMm: 8,
            requirePlacements: requirePlacements);

        Assert.Equal(expectedOk, gate.Ok);
        Assert.Equal(requirePlacements, gate.Errors.Any(e => e.StartsWith("nest_empty:")));
    }

    [Fact]
    public void Enabled_pip_slot_ignores_host_child_in_both_directions()
    {
        var panels = new[] { Rect("HOST", w: 300, h: 300), Rect("CHILD", w: 60, h: 40) };
        var placements = new[]
        {
            Place("HOST", x: 0, y: 0),
            Place("CHILD", x: 100, y: 100),
        };
        var slots = new[]
        {
            new PartInPartSlot
            {
                HostPanelId = "HOST",
                ChildPanelId = "CHILD",
                SheetIndex = 0,
                Enabled = true,
            },
        };

        var ignore = PartsInPartPacker.IgnoreCollisionPairs(slots);

        Assert.Contains(("HOST", "CHILD"), ignore);
        Assert.Contains(("CHILD", "HOST"), ignore);
        Assert.Empty(NestValidator.FindPolygonCollisions(panels, placements, 8, ignore));
        Assert.True(NestExportGate.Check(panels, placements, 8, partInPartSlots: slots).Ok);
    }

    [Fact]
    public void Disabled_pip_slot_does_not_hide_collision()
    {
        var panels = new[] { Rect("HOST", w: 300, h: 300), Rect("CHILD", w: 60, h: 40) };
        var placements = new[]
        {
            Place("HOST", x: 0, y: 0),
            Place("CHILD", x: 100, y: 100),
        };
        var slots = new[]
        {
            new PartInPartSlot
            {
                HostPanelId = "HOST",
                ChildPanelId = "CHILD",
                SheetIndex = 0,
                Enabled = false,
            },
        };

        var gate = NestExportGate.Check(panels, placements, 8, partInPartSlots: slots);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("aabb_gap:") || e.StartsWith("poly_gap:"));
    }

    [Fact]
    public void Pip_pair_does_not_hide_collision_with_third_part()
    {
        var panels = new[]
        {
            Rect("HOST", w: 300, h: 300),
            Rect("CHILD", w: 60, h: 40),
            Rect("OTHER", w: 60, h: 40),
        };
        var placements = new[]
        {
            Place("HOST", x: 0, y: 0),
            Place("CHILD", x: 100, y: 100),
            Place("OTHER", x: 100, y: 100),
        };
        var slots = new[]
        {
            new PartInPartSlot
            {
                HostPanelId = "HOST",
                ChildPanelId = "CHILD",
                SheetIndex = 0,
            },
        };

        var gate = NestExportGate.Check(panels, placements, 8, partInPartSlots: slots);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.Contains("OTHER"));
    }

    [Fact]
    public void Gate_rejects_placement_for_unknown_panel()
    {
        var gate = NestExportGate.Check(
            [Rect("KNOWN")],
            [Place("MISSING", x: 10, y: 10)],
            clearanceMm: 8);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("unknown_panel:"));
    }

    [Fact]
    public void Gate_rejects_duplicate_panel_placement()
    {
        var gate = NestExportGate.Check(
            [Rect("A")],
            [Place("A", x: 0, y: 0), Place("A", x: 200, y: 0)],
            clearanceMm: 8);

        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("duplicate_placement:"));
    }

    [Fact]
    public void Grouped_blf_conserves_all_input_parts_when_stock_exists()
    {
        var panels = Enumerable.Range(0, 12)
            .Select(i => Rect($"P{i}", i % 2 == 0 ? "oak" : "mdf", i % 3 == 0 ? 15 : 18, 120, 80))
            .ToList();
        var stock = new[]
        {
            Sheet("oak", 15),
            Sheet("oak", 18),
            Sheet("mdf", 15),
            Sheet("mdf", 18),
        };

        var result = GroupedBlfNester.Pack(
            panels,
            new NestSettings { MarginMm = 10, ClearanceMm = 8, AllowRotation = true },
            stock,
            GroupedBlfNester.SizeOfOutline);

        Assert.Equal(panels.Count, result.Placements.Count + result.Unplaced.Count);
        Assert.Equal(panels.Select(p => p.PanelId).Order(), result.Placements.Select(p => p.PanelId)
            .Concat(result.Unplaced).Order());
    }

    [Fact]
    public void Grouped_blf_conserves_parts_and_explains_missing_stock()
    {
        var panels = new[] { Rect("A", "oak", 18), Rect("B", "bamboo", 12) };

        var result = GroupedBlfNester.Pack(
            panels,
            new NestSettings(),
            [Sheet("oak", 18)],
            GroupedBlfNester.SizeOfOutline);

        Assert.Equal(panels.Length, result.Placements.Count + result.Unplaced.Count);
        Assert.Contains("B", result.Unplaced);
        Assert.Contains(result.UnplacedReasons, r => r.PanelId == "B" && r.Code == "no_stock_for_group");
    }

    static Panel Rect(
        string id,
        string material = "oak",
        double thickness = 18,
        double w = 100,
        double h = 100) =>
        new()
        {
            PanelId = id,
            Material = material,
            ThicknessMm = thickness,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
                Closed = true,
            },
        };

    static NestPlacement Place(
        string id,
        int sheet = 0,
        double x = 0,
        double y = 0,
        double rotation = 0) =>
        new()
        {
            PanelId = id,
            SheetIndex = sheet,
            OffsetX = x,
            OffsetY = y,
            RotationDeg = rotation,
        };

    static IReadOnlyList<NestPlacement> SeparatePlacements() =>
    [
        Place("A", x: 0, y: 0),
        Place("B", x: 200, y: 0),
    ];

    static NestSheetSpec Sheet(string material, double thickness) =>
        new()
        {
            WidthMm = 1220,
            LengthMm = 2440,
            BorderMm = 10,
            SpacingMm = 8,
            Material = material,
            ThicknessMm = thickness,
        };
}
