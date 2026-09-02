using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class GrainAlignTests
{
    static Panel Rect(string id, string? grain) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        GrainDirection = grain,
        Outline = new Outline
        {
            Points = [new Point2(0, 0), new Point2(100, 0), new Point2(100, 50), new Point2(0, 50)],
            Closed = true,
        },
    };

    [Fact]
    public void NormalizePart_reads_xy_and_none()
    {
        Assert.Equal("X", GrainAlign.NormalizePart("x"));
        Assert.Equal("Y", GrainAlign.NormalizePart("alongY"));
        Assert.Null(GrainAlign.NormalizePart("无"));
        Assert.Null(GrainAlign.NormalizePart("none"));
    }

    [Fact]
    public void Align_part_x_to_sheet_length_needs_90()
    {
        var settings = new NestSettings
        {
            AllowRotation = true,
            GrainLock = true,
            SheetGrain = SheetGrainKind.AlongLength,
        };
        var panel = Rect("A", "X");
        Assert.True(settings.PanelMayRotate90(panel));
        Assert.Equal(new[] { 90d, 270d }, settings.CandidateRotations(panel));
    }

    [Fact]
    public void Align_part_x_to_sheet_width_keeps_0_180()
    {
        var settings = new NestSettings
        {
            AllowRotation = true,
            GrainLock = true,
            SheetGrain = SheetGrainKind.AlongWidth,
        };
        var panel = Rect("A", "X");
        Assert.False(settings.PanelMayRotate90(panel));
        Assert.Equal(new[] { 0d, 180d }, settings.CandidateRotations(panel));
    }

    [Fact]
    public void No_sheet_grain_keeps_legacy_lock()
    {
        var settings = new NestSettings { AllowRotation = true, GrainLock = true };
        var panel = Rect("A", "Y");
        Assert.False(settings.PanelMayRotate90(panel));
        Assert.Equal(new[] { 0d, 180d }, settings.CandidateRotations(panel));
    }

    [Fact]
    public void WithGrain_clears_and_sets()
    {
        var next = Rect("A", null).WithGrain("Y");
        Assert.Equal("Y", next.GrainDirection);
        Assert.Equal("Y", next.Orientation?.GrainDirection);
        Assert.Null(next.WithGrain("none").GrainDirection);
    }

    [Fact]
    public void FromFusion_named_direction_wins()
    {
        Assert.Equal("Y", GrainAlign.FromFusion("Y", grainAngleDeg: 0, grainAlongMm: 200, 200, 595));
    }

    [Fact]
    public void FromFusion_angle_0_is_along_x()
    {
        Assert.Equal("X", GrainAlign.FromFusion(null, grainAngleDeg: 0, grainAlongMm: 200, 200, 595));
    }

    [Fact]
    public void FromFusion_angle_90_is_along_y()
    {
        Assert.Equal("Y", GrainAlign.FromFusion(null, grainAngleDeg: 90, grainAlongMm: null, 200, 595));
    }

    [Fact]
    public void FromFusion_along_mm_prefers_closer_edge()
    {
        Assert.Equal("X", GrainAlign.FromFusion(null, null, grainAlongMm: 934, 934, 54));
        Assert.Equal("Y", GrainAlign.FromFusion(null, null, grainAlongMm: 595, 200, 595));
        Assert.Null(GrainAlign.FromFusion(null, null, null, 200, 595));
    }
}
