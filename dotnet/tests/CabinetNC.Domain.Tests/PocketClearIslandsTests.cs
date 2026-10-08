using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class PocketClearIslandsTests
{
    static Point2[] Rect(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    static Panel PanelWith(params PanelFeature[] features) =>
        new()
        {
            PanelId = "P",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
            },
            Features = features,
        };

    [Fact]
    public void Full_thickness_pad_stays()
    {
        var pad = Rect(20, 20, 200, 140);
        var pocket = new PanelFeature
        {
            FeatureId = "PK",
            Kind = "pocket",
            DepthMm = 9,
            Path = Rect(0, 0, 220, 160),
            Holes = [pad],
        };
        var kept = PocketClearIslands.Keep(PanelWith(pocket), pocket);
        Assert.Single(kept);
        Assert.Equal(pad[0].X, kept[0][0].X);
    }

    [Fact]
    public void Thin_through_profile_is_ignored()
    {
        var slot = Rect(46, 40, 58, 260);
        var pocket = new PanelFeature
        {
            FeatureId = "PK",
            Kind = "pocket",
            DepthMm = 6,
            Path = Rect(0, 0, 104, 300),
            Holes = [slot],
        };
        var through = new PanelFeature
        {
            FeatureId = "SLOT",
            Kind = "cutout",
            Through = true,
            DepthMm = 18,
            Path = slot,
        };
        Assert.Empty(PocketClearIslands.Keep(PanelWith(pocket, through), pocket));
    }

    [Fact]
    public void Wide_through_window_stays_as_keepout()
    {
        var window = Rect(30, 30, 190, 130);
        var pocket = new PanelFeature
        {
            FeatureId = "PK",
            Kind = "pocket",
            DepthMm = 9,
            Path = Rect(10, 10, 210, 150),
            Holes = [window],
        };
        var through = new PanelFeature
        {
            FeatureId = "WIN",
            Kind = "cutout",
            Through = true,
            DepthMm = 18,
            Path = window,
        };
        var kept = PocketClearIslands.Keep(PanelWith(pocket, through), pocket);
        Assert.Single(kept);
    }

    [Fact]
    public void Wide_through_window_is_added_when_pocket_omits_holes()
    {
        var window = Rect(30, 30, 190, 130);
        var pocket = new PanelFeature
        {
            FeatureId = "PK",
            Kind = "pocket",
            DepthMm = 9,
            Path = Rect(10, 10, 210, 150),
        };
        var through = new PanelFeature
        {
            FeatureId = "WIN",
            Kind = "cutout",
            Through = true,
            DepthMm = 18,
            Path = window,
        };
        var kept = PocketClearIslands.Keep(PanelWith(pocket, through), pocket);
        Assert.Single(kept);
        Assert.True(kept[0].Max(p => p.X) - kept[0].Min(p => p.X) > 100);
    }

    [Fact]
    public void Thin_slot_on_a_pad_does_not_drop_the_pad()
    {
        var pad = Rect(20, 40, 84, 280);
        var slot = Rect(46, 50, 58, 270);
        var pocket = new PanelFeature
        {
            FeatureId = "PK",
            Kind = "pocket",
            DepthMm = 6,
            Path = Rect(0, 0, 104, 300),
            Holes = [pad],
        };
        var through = new PanelFeature
        {
            FeatureId = "SLOT",
            Kind = "cutout",
            Through = true,
            DepthMm = 18,
            Path = slot,
        };
        var kept = PocketClearIslands.Keep(PanelWith(pocket, through), pocket);
        Assert.Single(kept);
        Assert.Equal(64, kept[0].Max(p => p.X) - kept[0].Min(p => p.X), 3);
    }

    [Fact]
    public void Through_on_a_pad_does_not_add_a_second_keepout()
    {
        var pad = Rect(9, 9, 438, 268);
        var pocket = new PanelFeature
        {
            FeatureId = "REBATE",
            Kind = "pocket",
            DepthMm = 9,
            Path = Rect(0, 0, 447, 277),
            Holes = [pad],
        };
        var finger = new PanelFeature
        {
            FeatureId = "FINGER",
            Kind = "holeVertical",
            X = 223.5,
            Y = 138.5,
            DiameterMm = 40,
            DepthMm = 18,
            Through = true,
        };
        var kept = PocketClearIslands.Keep(PanelWith(pocket, finger), pocket);
        Assert.Single(kept);
    }
}
