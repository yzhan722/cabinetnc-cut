using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PanelDisplayTitleTests
{
    [Fact]
    public void DisplayTitle_skips_lay_flat_container_name()
    {
        var panel = new Panel
        {
            PanelId = "overhead.BP@layflat-1-24",
            Name = "LAY_FLAT:1 - LAY_FLAT",
            Outline = new Outline
            {
                Points = [new Point2(0, 0), new Point2(100, 0), new Point2(100, 50), new Point2(0, 50)],
                Closed = true,
            },
        };

        Assert.Equal("overhead.BP", panel.DisplayTitle);
        Assert.Equal("overhead", panel.DisplayGroup);
        Assert.Equal("BP", panel.DisplayPartName);
        Assert.True(Panel.IsLayFlatPlaceholder("LAY_FLAT:1 - LAY_FLAT"));
        Assert.False(Panel.IsLayFlatPlaceholder("overhead.BP"));
        Assert.Equal("manual.Body1", Panel.StripAtSuffix("manual.Body1@layflat-0-0"));
    }

    [Fact]
    public void DisplayGroup_splits_on_dot_or_hyphen()
    {
        var dotted = new Panel
        {
            PanelId = "generalTall.V1@layflat-1-3",
            Outline = new Outline
            {
                Points = [new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10)],
                Closed = true,
            },
        };
        Assert.Equal("generalTall", dotted.DisplayGroup);
        Assert.Equal("V1", dotted.DisplayPartName);

        var hyphened = new Panel
        {
            PanelId = "x",
            Name = "OHC_1-OH_BP",
            Outline = new Outline
            {
                Points = [new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10)],
                Closed = true,
            },
        };
        Assert.Equal("OHC_1", hyphened.DisplayGroup);
        Assert.Equal("OH_BP", hyphened.DisplayPartName);

        Assert.Equal(("Bunk_Tall_Right_1", "GT_H24_mid (2)"),
            Panel.SplitGroupPart("Bunk_Tall_Right_1-GT_H24_mid (2)"));
    }

    [Fact]
    public void MaterialGroupLabel_uses_shop_role_decor_surface_format()
    {
        var panel = new Panel
        {
            PanelId = "p1",
            Material = "carcass-white_stipple-15",
            ThicknessMm = 15,
            DecorId = "white_stipple",
            ColorName = "White Stipple",
            SurfaceMode = "DOUBLE_SIDED",
            Identity = new WorkpieceIdentity { Role = "carcass" },
            Outline = Rect(),
        };

        Assert.Equal("carcass-white_stipple-15", panel.MaterialGroupKey);
        Assert.Equal("Carcass_White Stipple_DS · 15mm", panel.MaterialGroupLabel);
    }

    [Fact]
    public void MaterialGroupLabel_defaults_surface_from_role_when_missing()
    {
        var carcass = new Panel
        {
            PanelId = "c1",
            Material = "carcass-white_stipple-15",
            ThicknessMm = 15,
            DecorId = "white_stipple",
            Identity = new WorkpieceIdentity { Role = "carcass" },
            Outline = Rect(),
        };
        Assert.Equal("Carcass_White Stipple_DS · 15mm", carcass.MaterialGroupLabel);

        var door = new Panel
        {
            PanelId = "d1",
            Material = "door-metallic_white-18",
            ThicknessMm = 18,
            DecorId = "metallic_white",
            Identity = new WorkpieceIdentity { Role = "door" },
            Outline = Rect(),
        };
        Assert.Equal("Door_Metallic White_SS · 18mm", door.MaterialGroupLabel);
    }

    static Outline Rect() => new()
    {
        Points = [new Point2(0, 0), new Point2(10, 0), new Point2(10, 10), new Point2(0, 10)],
        Closed = true,
    };
}
