using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class GroupedBlfNesterTests
{
    static Panel Rect(string id, string material, double thickness, double w, double h) => new()
    {
        PanelId = id,
        Material = material,
        ThicknessMm = thickness,
        Outline = new Outline
        {
            Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
        },
    };

    [Fact]
    public void Different_materials_never_share_sheet()
    {
        var panels = new[]
        {
            Rect("A", "oak", 18, 400, 300),
            Rect("B", "mdf", 18, 400, 300),
        };
        var stock = new[]
        {
            new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, Material = "oak", ThicknessMm = 18, Label = "oak18" },
            new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, Material = "mdf", ThicknessMm = 18, Label = "mdf18" },
        };
        var settings = new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true };
        var result = GroupedBlfNester.Pack(panels, settings, stock, GroupedBlfNester.SizeOfOutline);

        Assert.Equal(2, result.Placements.Count);
        Assert.True(result.SheetCount >= 2);
        var sheetA = result.Placements.Single(p => p.PanelId == "A").SheetIndex;
        var sheetB = result.Placements.Single(p => p.PanelId == "B").SheetIndex;
        Assert.NotEqual(sheetA, sheetB);
        Assert.Equal(2, result.GroupReports.Count);

        var gate = NestExportGate.Check(panels, result.Placements, settings.ClearanceMm);
        Assert.True(gate.Ok, string.Join("; ", gate.Errors));
    }

    [Fact]
    public void Different_thickness_never_share_sheet()
    {
        var panels = new[]
        {
            Rect("T15", "oak", 15, 400, 300),
            Rect("T18", "oak", 18, 400, 300),
        };
        var stock = new[]
        {
            new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, Material = "oak", ThicknessMm = 15 },
            new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, Material = "oak", ThicknessMm = 18 },
        };
        var result = GroupedBlfNester.Pack(panels, new NestSettings(), stock, GroupedBlfNester.SizeOfOutline);
        var s15 = result.Placements.Single(p => p.PanelId == "T15").SheetIndex;
        var s18 = result.Placements.Single(p => p.PanelId == "T18").SheetIndex;
        Assert.NotEqual(s15, s18);
    }

    [Fact]
    public void Missing_stock_reports_reason()
    {
        var panels = new[] { Rect("X", "bamboo", 12, 100, 100) };
        var stock = new[]
        {
            new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, Material = "oak", ThicknessMm = 18 },
        };
        var result = GroupedBlfNester.Pack(panels, new NestSettings(), stock, GroupedBlfNester.SizeOfOutline);
        Assert.Contains("X", result.Unplaced);
        Assert.Contains(result.UnplacedReasons, r => r.PanelId == "X" && r.Code == "no_stock_for_group");
    }

    [Fact]
    public void Export_gate_blocks_poly_collision()
    {
        var panels = new[]
        {
            Rect("A", "oak", 18, 100, 100),
            Rect("B", "oak", 18, 100, 100),
        };
        var placements = new[]
        {
            new NestPlacement { PanelId = "A", SheetIndex = 0, OffsetX = 0, OffsetY = 0 },
            new NestPlacement { PanelId = "B", SheetIndex = 0, OffsetX = 40, OffsetY = 0 },
        };
        var gate = NestExportGate.Check(panels, placements, clearanceMm: 12);
        Assert.False(gate.Ok);
        Assert.Contains(gate.Errors, e => e.StartsWith("poly_gap") || e.StartsWith("aabb_gap"));
    }

    [Fact]
    public void Grain_lock_disables_90_when_grain_set()
    {
        var settings = new NestSettings { AllowRotation = true, GrainLock = true };
        var panel = Rect("G", "oak", 18, 600, 400);
        panel = new Panel
        {
            PanelId = panel.PanelId,
            Material = panel.Material,
            ThicknessMm = panel.ThicknessMm,
            Outline = panel.Outline,
            GrainDirection = "X",
        };
        Assert.False(settings.PanelMayRotate90(panel));
        Assert.Empty(settings.ValidateConsistency());
        Assert.Equal(new[] { 0d, 180d }, settings.CandidateRotations(panel));
    }

    [Fact]
    public void CandidateRotations_include_180_when_rotation_allowed()
    {
        var settings = new NestSettings { AllowRotation = true, GrainLock = false };
        var panel = Rect("R", "oak", 18, 600, 400);
        Assert.Equal(new[] { 0d, 90d, 180d, 270d }, settings.CandidateRotations(panel));
        Assert.Equal(new[] { 0d }, new NestSettings { AllowRotation = false }.CandidateRotations(panel));
    }
}
