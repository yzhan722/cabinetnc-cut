namespace CabinetNC.Domain.Tests;

using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

public class PanelDraftCompileTests
{
    [Fact]
    public void Rect_profile_becomes_axis_aligned_panel()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 100, 40, 300, 180),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("DRAFT-1", "oak", 18));
        Assert.True(result.Ok, result.Error);
        var p = result.Panel!;
        Assert.Equal("DRAFT-1", p.PanelId);
        Assert.Equal("oak", p.Material);
        Assert.Equal(18, p.ThicknessMm);
        Assert.Equal(200, PanelEdit.BBox(p).W, 3);
        Assert.Equal(140, PanelEdit.BBox(p).H, 3);
        Assert.True(PanelEdit.IsAxisAlignedRect(p));
        Assert.Empty(p.Features);
    }

    [Fact]
    public void Inner_profile_is_through_cutout_and_feature_circle_is_hole()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 400, 300),
            Rect(DraftLayer.Profile, 80, 80, 160, 160),
            PanelDraftCompile.CircleFigure(DraftLayer.Feature, 300, 80, 4, depthMm: 18),
            new DraftFigure
            {
                Layer = DraftLayer.Feature,
                Points = [new(20, 20), new(200, 20)],
                Closed = false,
                DepthMm = 8,
                WidthMm = 6,
            },
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("P1", "carcass", 18));
        Assert.True(result.Ok, result.Error);
        var p = result.Panel!;
        Assert.Contains(p.Features, PanelEdit.IsCutout);
        Assert.Contains(p.Features, PanelEdit.IsHole);
        Assert.Contains(p.Features, PanelEdit.IsGroove);
        var hole = p.Features.First(PanelEdit.IsHole);
        Assert.Equal(8, hole.DiameterMm);
        Assert.True(hole.Through);
    }

    [Fact]
    public void Closed_feature_is_blind_pocket()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 200, 120),
            Rect(DraftLayer.Feature, 20, 20, 80, 50, depthMm: 6),
        };
        var result = PanelDraftCompile.TryBuild(figs, new DraftPanelRequest
        {
            PanelId = "P2",
            Name = "P2",
            Material = "door",
            ThicknessMm = 22,
        });
        Assert.True(result.Ok, result.Error);
        var pocket = result.Panel!.Features.Single(PanelEdit.IsPocket);
        Assert.False(pocket.Through);
        Assert.Equal(6, pocket.DepthMm);
    }

    [Fact]
    public void Open_profile_polyline_closes_into_panel()
    {
        var figs = new[]
        {
            new DraftFigure
            {
                Layer = DraftLayer.Profile,
                Closed = false,
                Points = [new(10, 10), new(210, 10), new(210, 130), new(10, 130)],
            },
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("OPEN", "oak", 18));
        Assert.True(result.Ok, result.Error);
        Assert.True(PanelEdit.IsAxisAlignedRect(result.Panel!));
        Assert.Equal(200, PanelEdit.BBox(result.Panel!).W, 3);
        Assert.Equal(120, PanelEdit.BBox(result.Panel!).H, 3);
    }

    [Fact]
    public void Missing_profile_fails()
    {
        var figs = new[]
        {
            PanelDraftCompile.CircleFigure(DraftLayer.Feature, 10, 10, 4, depthMm: 10),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("X", "oak", 18));
        Assert.False(result.Ok);
        Assert.Contains("Profile", result.Error);
    }

    [Fact]
    public void Explode_roundtrip_keeps_outline_and_hole()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 240, 160),
            PanelDraftCompile.CircleFigure(DraftLayer.Feature, 40, 40, 5, depthMm: 12),
        };
        var built = PanelDraftCompile.TryBuild(figs, Req("RT", "oak", 18));
        Assert.True(built.Ok, built.Error);
        var again = PanelDraftCompile.TryBuild(PanelDraftCompile.Explode(built.Panel!), Req("RT", "oak", 18));
        Assert.True(again.Ok, again.Error);
        Assert.Equal(PanelEdit.BBox(built.Panel!).W, PanelEdit.BBox(again.Panel!).W, 2);
        Assert.Single(again.Panel!.Features, PanelEdit.IsHole);
        Assert.Equal(12, again.Panel.Features.First(PanelEdit.IsHole).DepthMm);
    }

    [Fact]
    public void Side_by_side_profiles_are_rejected()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 200, 120),
            Rect(DraftLayer.Profile, 240, 0, 400, 120),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("TWO", "oak", 18));
        Assert.False(result.Ok);
        Assert.Contains("分开创建", result.Error);
    }

    [Fact]
    public void Cutout_island_is_kept_on_through_cutout()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 400, 300),
            Rect(DraftLayer.Profile, 40, 40, 280, 220),
            Rect(DraftLayer.Profile, 90, 80, 180, 160),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("ISL", "oak", 18));
        Assert.True(result.Ok, result.Error);
        var cut = result.Panel!.Features.Single(PanelEdit.IsCutout);
        Assert.True(cut.Through);
        Assert.Equal(18, cut.DepthMm);
        Assert.NotNull(cut.Holes);
        Assert.Single(cut.Holes!);
        Assert.True(cut.Holes![0].Count >= 3);
    }

    [Fact]
    public void Explode_roundtrip_keeps_cutout_island()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 400, 300),
            Rect(DraftLayer.Profile, 40, 40, 280, 220),
            Rect(DraftLayer.Profile, 90, 80, 180, 160),
        };
        var built = PanelDraftCompile.TryBuild(figs, Req("ISL-RT", "oak", 18));
        Assert.True(built.Ok, built.Error);
        var again = PanelDraftCompile.TryBuild(PanelDraftCompile.Explode(built.Panel!), Req("ISL-RT", "oak", 18));
        Assert.True(again.Ok, again.Error);
        var cut = again.Panel!.Features.Single(PanelEdit.IsCutout);
        Assert.NotNull(cut.Holes);
        Assert.Single(cut.Holes!);
    }

    [Fact]
    public void Profile_deeper_than_island_fails()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 400, 300),
            Rect(DraftLayer.Profile, 20, 20, 360, 260),
            Rect(DraftLayer.Profile, 60, 60, 280, 200),
            Rect(DraftLayer.Profile, 100, 90, 180, 150),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("DEEP", "oak", 18));
        Assert.False(result.Ok);
        Assert.Contains("三层", result.Error);
    }

    [Fact]
    public void Feature_without_depth_fails()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 100, 80),
            PanelDraftCompile.CircleFigure(DraftLayer.Feature, 20, 20, 4),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("ND", "oak", 18));
        Assert.False(result.Ok);
        Assert.Contains("深度", result.Error);
    }

    [Fact]
    public void Hole_depth_less_than_thickness_is_blind()
    {
        var figs = new[]
        {
            Rect(DraftLayer.Profile, 0, 0, 100, 80),
            PanelDraftCompile.CircleFigure(DraftLayer.Feature, 20, 20, 4, depthMm: 10),
        };
        var result = PanelDraftCompile.TryBuild(figs, Req("BL", "oak", 18));
        Assert.True(result.Ok, result.Error);
        var hole = result.Panel!.Features.Single(PanelEdit.IsHole);
        Assert.Equal(10, hole.DepthMm);
        Assert.False(hole.Through);
    }

    static DraftFigure Rect(DraftLayer layer, double x0, double y0, double x1, double y1, double? depthMm = null) =>
        new()
        {
            Layer = layer,
            Closed = true,
            DepthMm = depthMm,
            Points =
            [
                new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1), new(x0, y0),
            ],
        };

    static DraftPanelRequest Req(string id, string material, double thk) =>
        new()
        {
            PanelId = id,
            Name = id,
            Material = material,
            ThicknessMm = thk,
        };
}
