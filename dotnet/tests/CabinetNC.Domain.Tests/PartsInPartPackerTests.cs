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
    public void Duplicate_host_cutouts_do_not_stack_children()
    {
        var host = new Panel
        {
            PanelId = "HOST",
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "CUT1",
                    Kind = "throughCutout",
                    Through = true,
                    Path = [new(40, 40), new(360, 40), new(360, 260), new(40, 260)],
                },
                new PanelFeature
                {
                    FeatureId = "inner-1",
                    Kind = "throughCutout",
                    Through = true,
                    Purpose = "innerProfile",
                    Path = [new(40, 40), new(360, 40), new(360, 260), new(40, 260)],
                },
            ],
        };
        var a = Rect("A", 80, 200);
        var b = Rect("B", 80, 200);
        var stock = new[] { PipSheet() };

        var (packed, _) = new NestEngineRouter().Run(new NestEngineRequest
        {
            Panels = [host, a, b],
            Settings = new NestSettings { ClearanceMm = 8, MarginMm = 10, AllowRotation = true },
            StockTemplates = stock,
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        });

        Assert.Equal(2, packed.PartInPartSlots.Count);
        var pa = packed.Placements.Single(p => p.PanelId == "A");
        var pb = packed.Placements.Single(p => p.PanelId == "B");
        var dx = Math.Abs(pa.OffsetX - pb.OffsetX);
        var dy = Math.Abs(pa.OffsetY - pb.OffsetY);
        Assert.True(dx >= 80 + 8 - 1e-6 || dy >= 200 + 8 - 1e-6,
            $"children overlap A=({pa.OffsetX:0},{pa.OffsetY:0}) B=({pb.OffsetX:0},{pb.OffsetY:0})");
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

    [Fact]
    public void TryUsableVoid_matches_cutout_inset_by_clearance()
    {
        var host = HostWithCutout("HOST", 400, 300, 90, 60, 310, 240);
        var place = new NestPlacement
        {
            PanelId = "HOST",
            SheetIndex = 0,
            OffsetX = 15,
            OffsetY = 20,
            RotationDeg = 0,
        };
        Assert.True(PartsInPartPacker.TryUsableVoid(
            host, place.OffsetX, place.OffsetY, place.RotationDeg, "CUT1", 8,
            out var x, out var y, out var w, out var h));
        Assert.Equal(15 + 90 + 8, x, 6);
        Assert.Equal(20 + 60 + 8, y, 6);
        Assert.Equal(310 - 90 - 16, w, 6);
        Assert.Equal(240 - 60 - 16, h, 6);
    }

    [Fact]
    public void CenterInVoids_moves_corner_child_to_void_center()
    {
        var host = HostWithCutout("HOST", 400, 300, 90, 60, 310, 240);
        var child = Rect("CHILD", 80, 60);
        var byPanel = new Dictionary<string, Panel> { ["HOST"] = host, ["CHILD"] = child };
        var work = new List<NestPlacement>
        {
            new() { PanelId = "HOST", SheetIndex = 0, OffsetX = 15, OffsetY = 20, RotationDeg = 0 },
            new() { PanelId = "CHILD", SheetIndex = 0, OffsetX = 15 + 90 + 8, OffsetY = 20 + 60 + 8, RotationDeg = 0 },
        };
        var slots = new[]
        {
            new PartInPartSlot
            {
                HostPanelId = "HOST",
                ChildPanelId = "CHILD",
                FeatureId = "CUT1",
                SheetIndex = 0,
                Enabled = true,
            },
        };

        var moved = PartsInPartPacker.CenterInVoids(work, byPanel, slots, 0, clearanceMm: 8);
        Assert.Equal(1, moved);

        Assert.True(PartsInPartPacker.TryUsableVoid(
            host, 15, 20, 0, "CUT1", 8, out var vx, out var vy, out var vw, out var vh));
        var childPlace = work.Single(p => p.PanelId == "CHILD");
        Assert.Equal(vx + (vw - 80) / 2, childPlace.OffsetX, 6);
        Assert.Equal(vy + (vh - 60) / 2, childPlace.OffsetY, 6);
        Assert.Equal(15, work.Single(p => p.PanelId == "HOST").OffsetX, 6);
    }

    [Fact]
    public void CenterInVoids_keeps_relative_gap_of_two_children()
    {
        var host = HostWithCutout("HOST", 400, 300, 40, 30, 360, 270);
        var a = Rect("A", 80, 60);
        var b = Rect("B", 50, 40);
        var byPanel = new Dictionary<string, Panel> { ["HOST"] = host, ["A"] = a, ["B"] = b };
        const double gap = 8;
        var ax = 15 + 40 + 8;
        var ay = 20 + 30 + 8;
        var work = new List<NestPlacement>
        {
            new() { PanelId = "HOST", SheetIndex = 0, OffsetX = 15, OffsetY = 20, RotationDeg = 0 },
            new() { PanelId = "A", SheetIndex = 0, OffsetX = ax, OffsetY = ay, RotationDeg = 0 },
            new() { PanelId = "B", SheetIndex = 0, OffsetX = ax + 80 + gap, OffsetY = ay, RotationDeg = 0 },
        };
        var slots = new[]
        {
            new PartInPartSlot { HostPanelId = "HOST", ChildPanelId = "A", FeatureId = "CUT1", SheetIndex = 0 },
            new PartInPartSlot { HostPanelId = "HOST", ChildPanelId = "B", FeatureId = "CUT1", SheetIndex = 0 },
        };

        var moved = PartsInPartPacker.CenterInVoids(work, byPanel, slots, 0, clearanceMm: 8);
        Assert.Equal(2, moved);

        var pa = work.Single(p => p.PanelId == "A");
        var pb = work.Single(p => p.PanelId == "B");
        Assert.Equal(80 + gap, pb.OffsetX - pa.OffsetX, 6);
        Assert.Equal(0, pb.OffsetY - pa.OffsetY, 6);

        Assert.True(PartsInPartPacker.TryUsableVoid(
            host, 15, 20, 0, "CUT1", 8, out var vx, out var vy, out var vw, out var vh));
        var clusterW = 80 + gap + 50;
        var clusterH = 60;
        Assert.Equal(vx + (vw - clusterW) / 2, pa.OffsetX, 6);
        Assert.Equal(vy + (vh - clusterH) / 2, pa.OffsetY, 6);
    }

    [Fact]
    public void CenterInVoids_skips_locked_child()
    {
        var host = HostWithCutout("HOST", 400, 300, 90, 60, 310, 240);
        var child = Rect("CHILD", 80, 60);
        var byPanel = new Dictionary<string, Panel> { ["HOST"] = host, ["CHILD"] = child };
        var ox = 15 + 90 + 8;
        var oy = 20 + 60 + 8;
        var work = new List<NestPlacement>
        {
            new() { PanelId = "HOST", SheetIndex = 0, OffsetX = 15, OffsetY = 20, RotationDeg = 0 },
            new() { PanelId = "CHILD", SheetIndex = 0, OffsetX = ox, OffsetY = oy, RotationDeg = 0 },
        };
        var slots = new[]
        {
            new PartInPartSlot
            {
                HostPanelId = "HOST",
                ChildPanelId = "CHILD",
                FeatureId = "CUT1",
                SheetIndex = 0,
            },
        };
        var locked = new HashSet<string>(StringComparer.Ordinal) { "CHILD" };

        var moved = PartsInPartPacker.CenterInVoids(work, byPanel, slots, 0, 8, locked);
        Assert.Equal(0, moved);
        Assert.Equal(ox, work.Single(p => p.PanelId == "CHILD").OffsetX, 6);
        Assert.Equal(oy, work.Single(p => p.PanelId == "CHILD").OffsetY, 6);
    }
}
