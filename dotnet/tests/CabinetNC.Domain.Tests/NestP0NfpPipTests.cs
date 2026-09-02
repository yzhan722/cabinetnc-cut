using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestP0NfpPipTests
{
    [Fact]
    public void Nfp_placements_stay_inside_sheet_border_and_do_not_collide()
    {
        var panels = new[]
        {
            LShape("L1"),
            LShape("L2"),
            Rect("R1", 120, 80),
            Rect("R2", 90, 140),
        };
        var stock = Sheet(500, 500, border: 12);
        var settings = Settings(clearance: 6);

        var result = new ClipperNfpNestingEngine().Pack(
            panels, settings, [stock], GroupedBlfNester.SizeOfOutline);

        Assert.Equal(panels.Length, result.Placements.Count + result.Unplaced.Count);
        Assert.Empty(result.Unplaced);
        Assert.Empty(NestValidator.FindPolygonCollisions(panels, result.Placements, 6));
        Assert.All(result.Placements, p => AssertInsideSheet(
            panels.Single(x => x.PanelId == p.PanelId), p, stock));
    }

    [Fact]
    public void Nfp_avoids_blocked_region()
    {
        var panels = Enumerable.Range(0, 4).Select(i => Rect($"P{i}", 80, 80)).ToList();
        var blocked = new NestBlockedRect { MinX = 10, MinY = 10, MaxX = 190, MaxY = 190 };
        var stock = new NestSheetSpec
        {
            WidthMm = 400,
            LengthMm = 300,
            BorderMm = 10,
            SpacingMm = 6,
            Material = "oak",
            ThicknessMm = 18,
            Blocked = [blocked],
        };

        var result = new ClipperNfpNestingEngine().Pack(
            panels, Settings(6), [stock], GroupedBlfNester.SizeOfOutline);

        Assert.Empty(result.Unplaced);
        foreach (var placement in result.Placements)
        {
            var box = NestValidator.PlacementAabb(
                new NestPart { PanelId = placement.PanelId, WidthMm = 80, HeightMm = 80 },
                placement);
            Assert.False(NestValidator.AabbsConflict(
                box,
                (blocked.MinX, blocked.MinY, blocked.MaxX, blocked.MaxY),
                0));
        }
    }

    [Fact]
    public void Grain_lock_never_rotates_grained_panel_to_make_it_fit()
    {
        var panel = Rect("GRAIN", 160, 80, withGrain: true);
        var stock = Sheet(120, 210, border: 10);

        var result = new ClipperNfpNestingEngine().Pack(
            [panel],
            new NestSettings
            {
                MarginMm = 10,
                ClearanceMm = 4,
                AllowRotation = true,
                GrainLock = true,
            },
            [stock],
            GroupedBlfNester.SizeOfOutline);

        Assert.Empty(result.Placements);
        Assert.Contains("GRAIN", result.Unplaced);
    }

    [Fact]
    public void Without_grain_lock_panel_may_rotate_to_fit()
    {
        var panel = Rect("FREE", 160, 80);
        var stock = Sheet(120, 210, border: 10);

        var result = new ClipperNfpNestingEngine().Pack(
            [panel],
            new NestSettings
            {
                MarginMm = 10,
                ClearanceMm = 4,
                AllowRotation = true,
                GrainLock = false,
            },
            [stock],
            GroupedBlfNester.SizeOfOutline);

        var placement = Assert.Single(result.Placements);
        Assert.Equal(90, placement.RotationDeg);
    }

    [Fact]
    public void Allowed_rotations_without_quarter_turn_do_not_rotate_panel_to_fit()
    {
        var panel = Rect("LOCKED", 160, 80);
        var stock = Sheet(120, 210, border: 10);

        var result = new ClipperNfpNestingEngine().Pack(
            [panel],
            new NestSettings
            {
                MarginMm = 10,
                ClearanceMm = 4,
                AllowRotation = true,
                GrainLock = false,
                AllowedRotations = [0, 180],
            },
            [stock],
            GroupedBlfNester.SizeOfOutline);

        Assert.Empty(result.Placements);
        Assert.Contains("LOCKED", result.Unplaced);
    }

    [Fact]
    public void Nfp_is_deterministic_for_same_input()
    {
        var panels = Enumerable.Range(0, 8)
            .Select(i => i % 2 == 0 ? LShape($"P{i}") : Rect($"P{i}", 90 + i, 70 + i))
            .ToList();
        var stock = Sheet(600, 500, border: 10);
        var engine = new ClipperNfpNestingEngine();

        var first = engine.Pack(panels, Settings(5), [stock], GroupedBlfNester.SizeOfOutline);
        var second = engine.Pack(panels, Settings(5), [stock], GroupedBlfNester.SizeOfOutline);

        Assert.Equal(
            first.Placements.Select(Signature).ToList(),
            second.Placements.Select(Signature).ToList());
        Assert.Equal(first.Unplaced, second.Unplaced);
    }

    [Fact]
    public void Pip_rejects_child_that_misses_usable_void_by_fraction()
    {
        var host = Host("HOST", cutoutW: 100, cutoutH: 80);
        // Clearance 8 leaves 84x64 usable. Width misses by 0.001.
        var child = Rect("CHILD", 84.001, 64);

        var result = ApplyPipWithUnplacedChild(host, child, clearance: 8);

        Assert.Empty(result.PartInPartSlots);
        Assert.Contains("CHILD", result.Unplaced);
    }

    [Fact]
    public void Pip_accepts_child_at_exact_usable_void_boundary()
    {
        var host = Host("HOST", cutoutW: 100, cutoutH: 80);
        var child = Rect("CHILD", 84, 64);

        var result = ApplyPipWithUnplacedChild(host, child, clearance: 8);

        Assert.Single(result.PartInPartSlots);
        Assert.DoesNotContain("CHILD", result.Unplaced);
    }

    [Fact]
    public void Pip_rejects_non_through_cutout()
    {
        var host = Host("HOST", cutoutW: 160, cutoutH: 120, through: false);
        var child = Rect("CHILD", 60, 40);

        var result = ApplyPipWithUnplacedChild(host, child, clearance: 8);

        Assert.Empty(result.PartInPartSlots);
        Assert.Contains("CHILD", result.Unplaced);
    }

    [Theory]
    [InlineData("mdf", 18)]
    [InlineData("oak", 15)]
    public void Pip_rejects_different_stock_group(string childMaterial, double childThickness)
    {
        var host = Host("HOST", cutoutW: 160, cutoutH: 120);
        var child = Rect("CHILD", 60, 40, childMaterial, childThickness);

        var result = ApplyPipWithUnplacedChild(host, child, clearance: 8);

        Assert.Empty(result.PartInPartSlots);
        Assert.Contains("CHILD", result.Unplaced);
    }

    [Fact]
    public void Pip_rejects_void_below_minimum_dimension()
    {
        var host = Host("HOST", cutoutW: 30, cutoutH: 30);
        var child = Rect("CHILD", 10, 10);

        var result = ApplyPipWithUnplacedChild(host, child, clearance: 8);

        Assert.Empty(result.PartInPartSlots);
    }

    [Fact]
    public void Pip_handles_rotated_host_void()
    {
        var host = Host("HOST", cutoutW: 180, cutoutH: 120);
        var child = Rect("CHILD", 60, 40);
        var primary = Primary(host, child, hostRotation: 90);

        var result = PartsInPartPacker.Apply(
            primary,
            [host, child],
            Settings(8),
            [PipSheet()],
            GroupedBlfNester.SizeOfOutline);

        var slot = Assert.Single(result.PartInPartSlots);
        Assert.Equal("HOST", slot.HostPanelId);
        Assert.Equal("CHILD", slot.ChildPanelId);
        Assert.Equal(0, slot.SheetIndex);
    }

    [Fact]
    public void Multiple_pip_children_keep_clearance_from_each_other()
    {
        var host = Host("HOST", cutoutW: 260, cutoutH: 180);
        var a = Rect("A", 70, 50);
        var b = Rect("B", 70, 50);
        var primary = new NestResult
        {
            Engine = "fixture",
            Placements =
            [
                new NestPlacement
                {
                    PanelId = "HOST",
                    SheetIndex = 0,
                    OffsetX = 10,
                    OffsetY = 10,
                },
            ],
            SheetCount = 1,
            Unplaced = ["A", "B"],
            UnplacedReasons =
            [
                Reason("A"),
                Reason("B"),
            ],
            SheetsUsed = [PipSheet()],
        };

        var result = PartsInPartPacker.Apply(
            primary,
            [host, a, b],
            Settings(8),
            [PipSheet()],
            GroupedBlfNester.SizeOfOutline);

        Assert.Equal(2, result.PartInPartSlots.Count);
        var children = result.Placements.Where(p => p.PanelId is "A" or "B").ToList();
        var parts = new[]
        {
            new NestPart { PanelId = "A", WidthMm = 70, HeightMm = 50 },
            new NestPart { PanelId = "B", WidthMm = 70, HeightMm = 50 },
        };
        Assert.Empty(NestValidator.FindAabbCollisions(parts, children, 8));
    }

    static NestResult ApplyPipWithUnplacedChild(Panel host, Panel child, double clearance)
    {
        var primary = Primary(host, child);
        return PartsInPartPacker.Apply(
            primary,
            [host, child],
            Settings(clearance),
            [PipSheet()],
            GroupedBlfNester.SizeOfOutline);
    }

    static NestResult Primary(Panel host, Panel child, double hostRotation = 0) =>
        new()
        {
            Engine = "fixture",
            Placements =
            [
                new NestPlacement
                {
                    PanelId = host.PanelId,
                    SheetIndex = 0,
                    OffsetX = 10,
                    OffsetY = 10,
                    RotationDeg = hostRotation,
                },
            ],
            SheetCount = 1,
            Unplaced = [child.PanelId],
            UnplacedReasons = [Reason(child.PanelId)],
            SheetsUsed = [PipSheet()],
        };

    static NestUnplacedReason Reason(string id) =>
        new() { PanelId = id, Code = "fixture", Message = "fixture" };

    static NestSettings Settings(double clearance) =>
        new()
        {
            MarginMm = 10,
            ClearanceMm = clearance,
            AllowRotation = true,
            GrainLock = true,
        };

    static NestSheetSpec PipSheet() =>
        new()
        {
            WidthMm = 500,
            LengthMm = 400,
            BorderMm = 10,
            SpacingMm = 8,
            Material = "oak",
            ThicknessMm = 18,
            AllowPartsInPart = true,
        };

    static NestSheetSpec Sheet(double width, double height, double border) =>
        new()
        {
            WidthMm = width,
            LengthMm = height,
            BorderMm = border,
            SpacingMm = 6,
            Material = "oak",
            ThicknessMm = 18,
        };

    static Panel Rect(
        string id,
        double w,
        double h,
        string material = "oak",
        double thickness = 18,
        bool withGrain = false) =>
        new()
        {
            PanelId = id,
            Material = material,
            ThicknessMm = thickness,
            GrainDirection = withGrain ? "X" : null,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
                Closed = true,
            },
        };

    static Panel LShape(string id) =>
        new()
        {
            PanelId = id,
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points =
                [
                    new(0, 0), new(180, 0), new(180, 60),
                    new(60, 60), new(60, 180), new(0, 180),
                ],
                Closed = true,
            },
        };

    static Panel Host(
        string id,
        double cutoutW,
        double cutoutH,
        bool through = true)
    {
        const double minX = 70;
        const double minY = 60;
        return new Panel
        {
            PanelId = id,
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
                Closed = true,
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "CUT",
                    Kind = "cutout",
                    Through = through,
                    Path =
                    [
                        new(minX, minY),
                        new(minX + cutoutW, minY),
                        new(minX + cutoutW, minY + cutoutH),
                        new(minX, minY + cutoutH),
                    ],
                },
            ],
        };
    }

    static void AssertInsideSheet(Panel panel, NestPlacement placement, NestSheetSpec sheet)
    {
        var (w, h) = GroupedBlfNester.SizeOfOutline(panel);
        var box = NestValidator.PlacementAabb(
            new NestPart { PanelId = panel.PanelId, WidthMm = w, HeightMm = h },
            placement);
        Assert.True(box.minX >= sheet.BorderMm - 1e-6);
        Assert.True(box.minY >= sheet.BorderMm - 1e-6);
        Assert.True(box.maxX <= sheet.WidthMm - sheet.BorderMm + 1e-6);
        Assert.True(box.maxY <= sheet.LengthMm - sheet.BorderMm + 1e-6);
    }

    static string Signature(NestPlacement p) =>
        $"{p.PanelId}|{p.SheetIndex}|{p.OffsetX:0.###}|{p.OffsetY:0.###}|{p.RotationDeg:0.###}";
}
