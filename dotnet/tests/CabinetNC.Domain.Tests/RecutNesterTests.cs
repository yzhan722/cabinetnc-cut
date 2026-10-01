using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class RecutNesterTests
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

    static NestSheetSpec Remnant(string label, string material, double thickness, double w, double l) => new()
    {
        Label = label,
        Material = material,
        ThicknessMm = thickness,
        WidthMm = w,
        LengthMm = l,
        BorderMm = 10,
        SpacingMm = 10,
    };

    static (double w, double h) SizeOf(Panel p)
    {
        var pts = p.Outline.Points;
        return (pts.Max(q => q.X) - pts.Min(q => q.X), pts.Max(q => q.Y) - pts.Min(q => q.Y));
    }

    static readonly NestSettings Settings = new() { MarginMm = 10, ClearanceMm = 10, AllowRotation = true, GrainLock = false };

    [Fact]
    public void Stock_is_finite_and_never_cloned()
    {
        var panels = new[] { Rect("A", "oak", 18, 500, 300), Rect("B", "oak", 18, 500, 300) };
        var stock = new[] { Remnant("R1", "oak", 18, 600, 400) };

        var r = RecutNester.Pack(panels, stock, Settings, SizeOf);

        Assert.Single(r.Placements);
        Assert.Equal(1, r.SheetCount);
        var reason = Assert.Single(r.UnplacedReasons);
        Assert.Equal(RecutNester.NoFitCode, reason.Code);
    }

    [Fact]
    public void Large_part_skips_small_remnant_and_uses_the_next_one()
    {
        var panels = new[] { Rect("BIG", "oak", 18, 900, 700), Rect("SMALL", "oak", 18, 300, 200) };
        var stock = new[]
        {
            Remnant("small", "oak", 18, 600, 400),
            Remnant("large", "oak", 18, 1200, 800),
        };

        var r = RecutNester.Pack(panels, stock, Settings, SizeOf);

        Assert.Empty(r.Unplaced);
        var big = r.Placements.Single(p => p.PanelId == "BIG");
        var small = r.Placements.Single(p => p.PanelId == "SMALL");
        Assert.Equal("large", r.SheetsUsed[big.SheetIndex].Label);
        // The small remnant opened first still has room and is reused for the small part.
        Assert.Equal("small", r.SheetsUsed[small.SheetIndex].Label);
        Assert.Equal(2, r.SheetCount);
    }

    [Fact]
    public void Wrong_material_or_thickness_is_not_used()
    {
        var panels = new[] { Rect("A", "oak", 18, 300, 200) };
        var stock = new[]
        {
            Remnant("mdf", "mdf", 18, 1200, 800),
            Remnant("oak15", "oak", 15, 1200, 800),
        };

        var r = RecutNester.Pack(panels, stock, Settings, SizeOf);

        Assert.Empty(r.Placements);
        Assert.Equal(0, r.SheetCount);
        Assert.Equal(RecutNester.NoStockCode, Assert.Single(r.UnplacedReasons).Code);
    }

    [Fact]
    public void Unused_opened_sheets_are_dropped_and_indices_stay_dense()
    {
        // The first remnant is wide enough by bbox check but too narrow once the border is applied,
        // so it gets opened, fails, and must not linger as an empty sheet.
        var panels = new[] { Rect("A", "oak", 18, 590, 300) };
        var stock = new[]
        {
            Remnant("tight", "oak", 18, 600, 400),
            Remnant("ok", "oak", 18, 800, 400),
        };

        var r = RecutNester.Pack(panels, stock, Settings, SizeOf);

        var p = Assert.Single(r.Placements);
        Assert.Equal(0, p.SheetIndex);
        Assert.Equal("ok", Assert.Single(r.SheetsUsed).Label);
    }

    [Fact]
    public void Placements_stay_inside_remnant_and_do_not_overlap()
    {
        var panels = Enumerable.Range(0, 6).Select(i => Rect($"P{i}", "oak", 18, 280, 180)).ToList();
        var stock = new[] { Remnant("R", "oak", 18, 620, 620) };

        var r = RecutNester.Pack(panels, stock, Settings, SizeOf);

        Assert.True(r.Placements.Count >= 4, $"placed {r.Placements.Count}");
        var boxes = r.Placements.Select(p =>
        {
            var rot = Math.Abs(p.RotationDeg - 90) < 1e-6;
            var w = rot ? 180 : 280;
            var h = rot ? 280 : 180;
            Assert.True(p.OffsetX >= 10 - 1e-6 && p.OffsetY >= 10 - 1e-6);
            Assert.True(p.OffsetX + w <= 620 - 10 + 1e-6 && p.OffsetY + h <= 620 - 10 + 1e-6);
            return (p.OffsetX, p.OffsetY, w, h);
        }).ToList();
        for (var i = 0; i < boxes.Count; i++)
        for (var j = i + 1; j < boxes.Count; j++)
        {
            var a = boxes[i];
            var b = boxes[j];
            var overlap = a.OffsetX < b.OffsetX + b.w && b.OffsetX < a.OffsetX + a.w
                          && a.OffsetY < b.OffsetY + b.h && b.OffsetY < a.OffsetY + a.h;
            Assert.False(overlap, $"{i} overlaps {j}");
        }
    }

    [Fact]
    public void L_remnant_does_not_place_into_the_missing_corner()
    {
        Assert.True(RemnantOutline.TryBuild(
            [
                new Point2(0, 0), new Point2(800, 0), new Point2(800, 400),
                new Point2(400, 400), new Point2(400, 800), new Point2(0, 800),
            ],
            out var shape, out var err), err);

        var stock = new NestSheetSpec
        {
            Label = "L",
            Material = "oak",
            ThicknessMm = 18,
            WidthMm = shape!.WidthMm,
            LengthMm = shape.LengthMm,
            BorderMm = 10,
            SpacingMm = 10,
            Blocked = shape.Blocked,
        };

        // 500×500 fits the 800×800 bbox (after border) but not either arm of the L.
        var tooBig = RecutNester.Pack([Rect("BIG", "oak", 18, 500, 500)], [stock], Settings, SizeOf);
        Assert.Empty(tooBig.Placements);
        Assert.Equal(RecutNester.NoFitCode, Assert.Single(tooBig.UnplacedReasons).Code);

        var small = RecutNester.Pack([Rect("S", "oak", 18, 300, 200)], [stock], Settings, SizeOf);
        var p = Assert.Single(small.Placements);
        Assert.True(p.OffsetX + 300 <= 400 + 1e-6 || p.OffsetY + 200 <= 400 + 1e-6,
            $"placed into the notch at {p.OffsetX:0.#},{p.OffsetY:0.#}");
    }
}
