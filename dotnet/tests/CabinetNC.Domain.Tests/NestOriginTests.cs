using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestOriginTests
{
    static Panel L(string id) => new()
    {
        PanelId = id,
        Material = "m",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points =
            [
                new(0, 0), new(600, 0), new(600, 200),
                new(200, 200), new(200, 900), new(0, 900),
            ],
        },
    };

    static Panel Rect(string id, double w, double h) => new()
    {
        PanelId = id,
        Material = "m",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
    };

    static NestSheetSpec Sheet() => new()
    {
        WidthMm = 1220,
        LengthMm = 2440,
        BorderMm = 15,
        SpacingMm = 12,
        Material = "m",
        ThicknessMm = 18,
        AllowRotation = true,
    };

    static NestResult Run(IReadOnlyList<Panel> panels, bool right) =>
        new NestEngineRouter(advanced: new ClipperNfpNestingEngine()).Run(new NestEngineRequest
        {
            Panels = panels,
            Settings = new NestSettings { ClearanceMm = 12, MarginMm = 15, AllowRotation = true },
            StockTemplates = [Sheet()],
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "nfp",
            AdvancedTimeout = TimeSpan.FromSeconds(30),
            OriginRight = right,
        }).Result;

    static double SignedArea(IReadOnlyList<(double X, double Y)> pts)
    {
        double s = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s;
    }

    [Fact]
    public void Right_origin_keeps_the_part_shape_and_hugs_the_right_edge()
    {
        var part = L("L");
        var placed = Assert.Single(Run([part], right: true).Placements);
        var world = NestTransform.SheetOutline(part, placed.OffsetX, placed.OffsetY, placed.RotationDeg)
            .Select(p => (p.X, p.Y)).ToList();
        var local = part.Outline.Points.Select(p => (p.X, p.Y)).ToList();

        Assert.True(SignedArea(world) * SignedArea(local) > 0, "placed outline is mirrored");
        Assert.Equal(1220 - 15, world.Max(p => p.X), 3);
        Assert.Equal(15, world.Min(p => p.Y), 3);
    }

    [Fact]
    public void Right_origin_pack_has_no_spacing_errors()
    {
        Panel[] panels = [L("L1"), L("L2"), Rect("R1", 400, 250), Rect("R2", 180, 700)];
        var packed = Run(panels, right: true);
        Assert.Equal(panels.Length, packed.Placements.Count);
        var gate = NestExportGate.Check(panels, packed.Placements, 12, allowAabbOverlap: true);
        Assert.True(gate.Ok, string.Join("; ", gate.Errors));
    }
}
