using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PartsInPartPackerTests
{
    static Panel HostWithCutout(string id, double w, double h, double cutMinX, double cutMinY, double cutMaxX, double cutMaxY) =>
        new()
        {
            PanelId = id,
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "CUT1",
                    Kind = "throughCutout",
                    Through = true,
                    Purpose = "innerProfile",
                    Path =
                    [
                        new(cutMinX, cutMinY),
                        new(cutMaxX, cutMinY),
                        new(cutMaxX, cutMaxY),
                        new(cutMinX, cutMaxY),
                    ],
                },
            ],
        };

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

    static NestSheetSpec PipSheet() => new()
    {
        WidthMm = 1220,
        LengthMm = 2440,
        BorderMm = 10,
        SpacingMm = 8,
        AllowPartsInPart = true,
        Material = "oak",
        ThicknessMm = 18,
    };

    [Fact]
    public void Relocates_small_part_into_host_cutout_when_pip_enabled()
    {
        // Host 400×300 with 220×180 opening; child 80×60 fits inside after clearance.
        var host = HostWithCutout("HOST", 400, 300, 90, 60, 310, 240);
        var child = Rect("CHILD", 80, 60);
        var panels = new[] { host, child };
        var settings = new NestSettings { ClearanceMm = 8, MarginMm = 10, AllowRotation = true };
        var stock = new[] { PipSheet() };

        var (packed, _) = new NestEngineRouter().Run(new NestEngineRequest
        {
            Panels = panels,
            Settings = settings,
            StockTemplates = stock,
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        });

        Assert.Equal(2, packed.Placements.Count);
        Assert.NotEmpty(packed.PartInPartSlots);
        var slot = Assert.Single(packed.PartInPartSlots);
        Assert.Equal("HOST", slot.HostPanelId);
        Assert.Equal("CHILD", slot.ChildPanelId);

        var hostPlace = packed.Placements.Single(p => p.PanelId == "HOST");
        var childPlace = packed.Placements.Single(p => p.PanelId == "CHILD");
        Assert.Equal(hostPlace.SheetIndex, childPlace.SheetIndex);

        // Child AABB should sit inside the usable void (cutout inset by clearance).
        var hb = NestTransform.BoundsOf(host);
        var cutSheet = host.Features[0].Path!.Select(pt =>
            NestTransform.ToSheet(pt.X, pt.Y, hb, hostPlace.OffsetX, hostPlace.OffsetY, hostPlace.RotationDeg))
            .ToList();
        var voidMinX = cutSheet.Min(p => p.X) + settings.ClearanceMm;
        var voidMinY = cutSheet.Min(p => p.Y) + settings.ClearanceMm;
        var voidMaxX = cutSheet.Max(p => p.X) - settings.ClearanceMm;
        var voidMaxY = cutSheet.Max(p => p.Y) - settings.ClearanceMm;
        Assert.True(childPlace.OffsetX >= voidMinX - 1e-6);
        Assert.True(childPlace.OffsetY >= voidMinY - 1e-6);
        Assert.True(childPlace.OffsetX + 80 <= voidMaxX + 1e-6);
        Assert.True(childPlace.OffsetY + 60 <= voidMaxY + 1e-6);

        var gate = NestExportGate.Check(
            panels, packed.Placements, settings.ClearanceMm,
            partInPartSlots: packed.PartInPartSlots);
        Assert.True(gate.Ok, string.Join("; ", gate.Errors));
    }

    [Fact]
    public void Skips_when_pip_disabled()
    {
        var host = HostWithCutout("HOST", 400, 300, 90, 60, 310, 240);
        var child = Rect("CHILD", 80, 60);
        var stock = new[]
        {
            new NestSheetSpec
            {
                WidthMm = 1220,
                LengthMm = 2440,
                BorderMm = 10,
                AllowPartsInPart = false,
                Material = "oak",
                ThicknessMm = 18,
            },
        };

        var (packed, _) = new NestEngineRouter().Run(new NestEngineRequest
        {
            Panels = [host, child],
            Settings = new NestSettings { ClearanceMm = 8, MarginMm = 10 },
            StockTemplates = stock,
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        });

        Assert.Empty(packed.PartInPartSlots);
        var hostPlace = packed.Placements.Single(p => p.PanelId == "HOST");
        var childPlace = packed.Placements.Single(p => p.PanelId == "CHILD");
        // Without PIP they remain side-by-side (child not inside host AABB).
        var childInsideHost =
            childPlace.OffsetX >= hostPlace.OffsetX
            && childPlace.OffsetY >= hostPlace.OffsetY
            && childPlace.OffsetX + 80 <= hostPlace.OffsetX + 400
            && childPlace.OffsetY + 60 <= hostPlace.OffsetY + 300;
        Assert.False(childInsideHost);
    }

    [Fact]
    public void Can_reduce_sheet_count_by_moving_child_into_void()
    {
        // Tiny sheet forces host and child onto separate sheets without PIP;
        // with PIP the child should rehome into the host void → 1 sheet.
        var host = HostWithCutout("HOST", 400, 300, 40, 40, 360, 260);
        var child = Rect("CHILD", 100, 80);
        var stock = new[]
        {
            new NestSheetSpec
            {
                WidthMm = 430,
                LengthMm = 330,
                BorderMm = 10,
                SpacingMm = 6,
                AllowPartsInPart = true,
                Material = "oak",
                ThicknessMm = 18,
            },
        };

        var (packed, _) = new NestEngineRouter().Run(new NestEngineRequest
        {
            Panels = [host, child],
            Settings = new NestSettings { ClearanceMm = 6, MarginMm = 10, AllowRotation = true },
            StockTemplates = stock,
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        });

        Assert.NotEmpty(packed.PartInPartSlots);
        Assert.Equal(1, packed.SheetCount);
        Assert.Equal(2, packed.Placements.Count);
    }
}
