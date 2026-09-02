using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Materials;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using WorkpieceIdentity = CabinetNC.Domain.Parts.WorkpieceIdentity;

namespace CabinetNC.Domain.Tests;

public class MaterialCorrectTests
{
    static Panel Box(
        string id,
        double thickness,
        string? material = "carcass",
        params PanelFeature[] features) =>
        new()
        {
            PanelId = id,
            Name = "Side",
            Material = material,
            ThicknessMm = thickness,
            Quantity = 1,
            Outline = new Outline
            {
                Points = [new Point2(0, 0), new Point2(100, 0), new Point2(100, 50), new Point2(0, 50)],
                Closed = true,
            },
            Features = features,
        };

    static PanelFeature Feat(
        string id,
        string kind,
        double depth,
        bool through = false,
        string? purpose = null,
        double? diameter = null) =>
        new()
        {
            FeatureId = id,
            Kind = kind,
            Through = through,
            Purpose = purpose,
            DepthMm = depth,
            DiameterMm = diameter,
            X = 10,
            Y = 10,
            Path = kind.Contains("groove", StringComparison.OrdinalIgnoreCase)
                ? [new Point2(0, 10), new Point2(80, 10)]
                : null,
        };

    static CutPackage Pkg(params Panel[] panels) => new()
    {
        SchemaName = CutPackage.Schema,
        JobId = "Club",
        Sheets =
        [
            new SheetStock { SheetId = "S15", Material = "carcass", ThicknessMm = 15, WidthMm = 1220, LengthMm = 2440 },
            new SheetStock { SheetId = "S145", Material = "carcass", ThicknessMm = 14.5, WidthMm = 1220, LengthMm = 2440 },
        ],
        Panels = panels,
    };

    static NestGroupKey K(double t) => NestGroupKey.From("carcass", t);

    [Fact]
    public void Merge_rewrites_through_and_full_slot_to_target_thickness()
    {
        var pkg = Pkg(
            Box("A", 15),
            Box("B", 14.5,
                features:
                [
                    Feat("H1", "holeVertical", 14.5, through: true),
                    Feat("S1", "grooveVertical", 14.5, through: true),
                ]));
        var merged = MaterialCorrect.MergeKinds(pkg, [K(15), K(14.5)], K(15), BlindFeatureDepthPolicy.Keep);
        var b = merged.Panels.Single(p => p.PanelId == "B");
        Assert.Equal(15, b.ThicknessMm);
        Assert.Equal("carcass", b.Material);
        Assert.Equal(15, b.Features.Single(f => f.FeatureId == "H1").DepthMm);
        Assert.True(b.Features.Single(f => f.FeatureId == "H1").Through);
        Assert.Equal(15, b.Features.Single(f => f.FeatureId == "S1").DepthMm);
        Assert.Single(merged.Sheets);
        Assert.Equal(15, merged.Sheets[0].ThicknessMm);
    }

    [Fact]
    public void Keep_leaves_half_slot_and_hinge_depth()
    {
        var pkg = Pkg(
            Box("A", 15),
            Box("B", 14.5,
                features:
                [
                    Feat("T1", "grooveVertical", 7.25, purpose: "tongue"),
                    Feat("C1", "holeVertical", 12, purpose: "hinge", diameter: 35),
                ]));
        var merged = MaterialCorrect.MergeKinds(pkg, [K(15), K(14.5)], K(15), BlindFeatureDepthPolicy.Keep);
        var b = merged.Panels.Single(p => p.PanelId == "B");
        Assert.Equal(7.25, b.Features.Single(f => f.FeatureId == "T1").DepthMm);
        Assert.Equal(12, b.Features.Single(f => f.FeatureId == "C1").DepthMm);
    }

    [Fact]
    public void Scale_adjusts_half_slot_and_hinge_with_thickness()
    {
        var pkg = Pkg(
            Box("A", 15),
            Box("B", 14.5,
                features:
                [
                    Feat("T1", "grooveVertical", 7.25, purpose: "tongue"),
                    Feat("C1", "holeVertical", 12, purpose: "hinge", diameter: 35),
                ]));
        var merged = MaterialCorrect.MergeKinds(pkg, [K(15), K(14.5)], K(15), BlindFeatureDepthPolicy.ScaleWithThickness);
        var b = merged.Panels.Single(p => p.PanelId == "B");
        Assert.Equal(7.5, b.Features.Single(f => f.FeatureId == "T1").DepthMm!.Value, 3);
        Assert.Equal(12 * 15 / 14.5, b.Features.Single(f => f.FeatureId == "C1").DepthMm!.Value, 3);
    }

    [Fact]
    public void HasHalfSlotOrHinge_false_when_only_through()
    {
        var panels = new[]
        {
            Box("B", 14.5, features: [Feat("H1", "holeVertical", 14.5, through: true)]),
        };
        Assert.False(MaterialCorrect.HasHalfSlotOrHinge(panels));
    }

    [Fact]
    public void Target_panels_are_left_alone()
    {
        var pkg = Pkg(Box("A", 15, features: [Feat("T1", "grooveVertical", 7.5, purpose: "tongue")]));
        var merged = MaterialCorrect.MergeKinds(pkg, [K(15), K(14.5)], K(15), BlindFeatureDepthPolicy.ScaleWithThickness);
        Assert.Equal(7.5, merged.Panels[0].Features[0].DepthMm);
        Assert.Equal(15, merged.Panels[0].ThicknessMm);
    }

    [Fact]
    public void Retarget_one_panel_keeps_other_kind_and_sheet()
    {
        var pkg = Pkg(
            Box("A", 15),
            Box("B", 14.5, features: [Feat("H1", "holeVertical", 14.5, through: true)]),
            Box("C", 14.5));
        var next = MaterialCorrect.RetargetPanels(pkg, ["B"], K(15), BlindFeatureDepthPolicy.Keep);
        var b = next.Panels.Single(p => p.PanelId == "B");
        var c = next.Panels.Single(p => p.PanelId == "C");
        Assert.Equal(15, b.ThicknessMm);
        Assert.Equal(15, b.Features.Single(f => f.FeatureId == "H1").DepthMm);
        Assert.Equal(14.5, c.ThicknessMm);
        Assert.Equal(2, next.Sheets.Count);
        Assert.Contains(next.Sheets, s => Math.Abs(s.ThicknessMm - 15) < 0.01);
        Assert.Contains(next.Sheets, s => Math.Abs(s.ThicknessMm - 14.5) < 0.01);
    }

    [Fact]
    public void Retarget_last_panel_drops_empty_kind_sheet()
    {
        var pkg = Pkg(Box("A", 15), Box("B", 14.5));
        var next = MaterialCorrect.RetargetPanels(pkg, ["B"], K(15), BlindFeatureDepthPolicy.Keep);
        Assert.Equal(15, next.Panels.Single(p => p.PanelId == "B").ThicknessMm);
        Assert.Single(next.Sheets);
        Assert.Equal(15, next.Sheets[0].ThicknessMm);
    }

    [Fact]
    public void Retarget_same_kind_is_noop()
    {
        var pkg = Pkg(Box("A", 15));
        var next = MaterialCorrect.RetargetPanels(pkg, ["A"], K(15), BlindFeatureDepthPolicy.Keep);
        Assert.Same(pkg, next);
    }

    [Fact]
    public void Retarget_copies_donor_kind_identity()
    {
        var oak = Box("A", 18, "oak");
        oak = new Panel
        {
            PanelId = oak.PanelId,
            Name = oak.Name,
            Material = oak.Material,
            ThicknessMm = oak.ThicknessMm,
            DecorId = "oak_veneer",
            SubstrateId = "door_board",
            ColorName = "Oak",
            SurfaceMode = "SINGLE_SIDED",
            Quantity = 1,
            Outline = oak.Outline,
            Features = oak.Features,
            Identity = new WorkpieceIdentity { Role = "door", WorkpieceId = "A" },
        };
        var white = Box("B", 15, "carcass", Feat("H1", "holeVertical", 15, through: true));
        white = new Panel
        {
            PanelId = white.PanelId,
            Name = white.Name,
            Material = white.Material,
            ThicknessMm = white.ThicknessMm,
            DecorId = "white_stipple",
            SubstrateId = "carcass_board",
            ColorName = "White Stipple",
            SurfaceMode = "DOUBLE_SIDED",
            Quantity = 1,
            Outline = white.Outline,
            Features = white.Features,
            Identity = new WorkpieceIdentity { Role = "carcass", WorkpieceId = "B" },
        };
        var pkg = Pkg(oak, white);
        var next = MaterialCorrect.RetargetPanels(
            pkg, ["B"], NestGroupKey.From("oak", 18), BlindFeatureDepthPolicy.Keep);
        var b = next.Panels.Single(p => p.PanelId == "B");
        Assert.Equal("oak", b.Material);
        Assert.Equal(18, b.ThicknessMm);
        Assert.Equal("oak_veneer", b.DecorId);
        Assert.Equal("Oak", b.ColorName);
        Assert.Equal("door_board", b.SubstrateId);
        Assert.Equal("SINGLE_SIDED", b.SurfaceMode);
        Assert.Equal("door", b.Identity?.Role);
        Assert.Equal(18, b.Features.Single(f => f.FeatureId == "H1").DepthMm);
    }
}
