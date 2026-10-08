using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class LabelAnchorFinderTests
{
    static Panel Rect(string id, double w, double h, params PanelFeature[] features) =>
        new()
        {
            PanelId = id,
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
            },
            Features = features,
        };

    [Fact]
    public void Empty_rect_uses_outline_centroid()
    {
        var a = LabelAnchorFinder.Find(Rect("EMPTY", 400, 300));
        Assert.True(a.FitsComfortably);
        Assert.True(a.FitsAtAll);
        Assert.Equal(200, a.LocalX, 1);
        Assert.Equal(150, a.LocalY, 1);
        Assert.Equal(60, a.WidthMm);
        Assert.Equal(60, a.HeightMm);
    }

    [Fact]
    public void Center_cutout_does_not_place_in_the_opening()
    {
        var panel = Rect("CUTOUT", 400, 300,
            new PanelFeature
            {
                FeatureId = "WIN",
                Kind = "cutout",
                Through = true,
                Path = [new(150, 100), new(250, 100), new(250, 200), new(150, 200)],
            });
        var a = LabelAnchorFinder.Find(panel);
        Assert.True(a.FitsComfortably);
        Assert.False(Inside(150, 100, 250, 200, a.LocalX, a.LocalY));
        Assert.InRange(a.LocalX, 40, 360);
        Assert.InRange(a.LocalY, 30, 270);
        Assert.True(Math.Abs(a.LocalX - 200) > 8 || Math.Abs(a.LocalY - 150) > 8);
    }

    [Fact]
    public void Center_cutout_stays_off_the_opening_rim()
    {
        var panel = Rect("CUTOUT-RIM", 400, 300,
            new PanelFeature
            {
                FeatureId = "WIN",
                Kind = "cutout",
                Through = true,
                Path = [new(150, 100), new(250, 100), new(250, 200), new(150, 200)],
            });
        var a = LabelAnchorFinder.Find(panel);
        var gapX = Math.Min(Math.Abs(a.LocalX - 150), Math.Abs(a.LocalX - 250));
        var gapY = Math.Min(Math.Abs(a.LocalY - 100), Math.Abs(a.LocalY - 200));
        var outsideX = a.LocalX <= 150 || a.LocalX >= 250;
        var outsideY = a.LocalY <= 100 || a.LocalY >= 200;
        if (outsideX && !outsideY)
            Assert.True(gapX > LabelAnchorFinder.InteriorClearanceMm * 0.5);
        if (outsideY && !outsideX)
            Assert.True(gapY > LabelAnchorFinder.InteriorClearanceMm * 0.5);
    }

    [Fact]
    public void Hinge_cup_at_center_is_avoided()
    {
        var panel = Rect("HINGE", 400, 300,
            new PanelFeature
            {
                FeatureId = "H1",
                Kind = "holeVertical",
                Purpose = "hinge",
                X = 200,
                Y = 150,
                DiameterMm = 35,
                DepthMm = 13,
            });
        var a = LabelAnchorFinder.Find(panel);
        Assert.True(a.FitsComfortably, $"fit {a.FitsComfortably}/{a.FitsAtAll} at {a.LocalX:0.#},{a.LocalY:0.#}");
        var d = Math.Sqrt((a.LocalX - 200) * (a.LocalX - 200) + (a.LocalY - 150) * (a.LocalY - 150));
        Assert.True(d > 17.5 + LabelAnchorFinder.KeepOutInflateMm,
            $"anchor ({a.LocalX:0.#},{a.LocalY:0.#}) still in cup d={d:0.#}");
    }

    [Fact]
    public void Half_slot_across_center_is_avoided()
    {
        var panel = Rect("HALFSLOT", 400, 300,
            new PanelFeature
            {
                FeatureId = "G1",
                Kind = "grooveVertical",
                Purpose = "tongue",
                WidthMm = 16,
                DepthMm = 8,
                Path = [new(0, 150), new(400, 150)],
            });
        var a = LabelAnchorFinder.Find(panel);
        Assert.True(a.FitsComfortably);
        Assert.True(Math.Abs(a.LocalY - 150) > 8 + LabelAnchorFinder.KeepOutInflateMm);
    }

    [Fact]
    public void Unmarked_red_groove_across_center_is_avoided()
    {
        var panel = Rect("REDSLOT", 400, 300,
            new PanelFeature
            {
                FeatureId = "G1",
                Kind = "grooveVertical",
                WidthMm = 16,
                DepthMm = 8,
                Path = [new(200, 20), new(200, 280)],
            });
        var a = LabelAnchorFinder.Find(panel);
        Assert.True(a.FitsComfortably);
        Assert.True(Math.Abs(a.LocalX - 200) > 8 + LabelAnchorFinder.KeepOutInflateMm,
            $"anchor ({a.LocalX:0.#},{a.LocalY:0.#}) still on the red slot");
    }

    [Fact]
    public void Led_channel_across_center_is_avoided()
    {
        var panel = Rect("LED", 400, 300,
            new PanelFeature
            {
                FeatureId = "LED1",
                Kind = "pocket",
                Purpose = "led",
                DepthMm = 6,
                Path = [new(180, 20), new(220, 20), new(220, 280), new(180, 280)],
            });
        var a = LabelAnchorFinder.Find(panel);
        Assert.True(a.FitsComfortably);
        Assert.False(Inside(180, 20, 220, 280, a.LocalX, a.LocalY),
            $"anchor ({a.LocalX:0.#},{a.LocalY:0.#}) still on the LED channel");
    }

    [Fact]
    public void Label_stays_60_by_60_and_does_not_rotate()
    {
        // 50 mm wide: a 60 mm square does not fit. Printer orientation is fixed.
        var a = LabelAnchorFinder.Find(Rect("NOROT", 50, 200));
        Assert.Equal(60, a.WidthMm);
        Assert.Equal(60, a.HeightMm);
        Assert.False(a.FitsAtAll);
    }

    [Fact]
    public void Override_on_empty_rect_keeps_point()
    {
        var a = LabelAnchorFinder.Find(Rect("OV", 400, 300), 0, (80, 80));
        Assert.True(a.FitsComfortably);
        Assert.Equal(80, a.LocalX, 1);
        Assert.Equal(80, a.LocalY, 1);
    }

    [Fact]
    public void Override_on_slot_snaps_away()
    {
        var panel = Rect("OVSLOT", 400, 300,
            new PanelFeature
            {
                FeatureId = "G1",
                Kind = "grooveVertical",
                WidthMm = 16,
                DepthMm = 8,
                Path = [new(200, 20), new(200, 280)],
            });
        var a = LabelAnchorFinder.Find(panel, 0, (200, 150));
        Assert.True(a.FitsComfortably);
        Assert.True(Math.Abs(a.LocalX - 200) > 8 + LabelAnchorFinder.KeepOutInflateMm,
            $"override stayed on the slot at ({a.LocalX:0.#},{a.LocalY:0.#})");
    }

    [Fact]
    public void ToSheet_FromSheet_roundtrip()
    {
        var panel = Rect("ROUNDTRIP", 400, 300);
        var bounds = NestTransform.BoundsOf(panel);
        var (sx, sy) = NestTransform.ToSheet(80, 40, bounds, 12, 9, 90);
        var (lx, ly) = NestTransform.FromSheet(sx, sy, bounds, 12, 9, 90);
        Assert.Equal(80, lx, 3);
        Assert.Equal(40, ly, 3);
    }

    static bool Inside(double x0, double y0, double x1, double y1, double x, double y) =>
        x >= x0 && x <= x1 && y >= y0 && y <= y1;
}
