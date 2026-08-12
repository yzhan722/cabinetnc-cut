using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class GrooveGeometryTests
{
    [Fact]
    public void Outline_from_vertical_centerline_is_rectangle_of_given_width()
    {
        var outline = GrooveGeometry.OutlineFromCenterline(
            [new Point2(10, 0), new Point2(10, 100)],
            widthMm: 6);

        Assert.Equal(4, outline.Count);
        var xs = outline.Select(p => p.X).OrderBy(v => v).ToList();
        var ys = outline.Select(p => p.Y).OrderBy(v => v).ToList();
        Assert.Equal(7, xs.First(), 3);
        Assert.Equal(13, xs.Last(), 3);
        Assert.Equal(0, ys.First(), 3);
        Assert.Equal(100, ys.Last(), 3);
    }

    [Fact]
    public void Outline_swaps_axis_when_width_longer_than_centerline()
    {
        // Exported centreline is only 6mm; widthMm incorrectly holds the length.
        var outline = GrooveGeometry.OutlineFromCenterline(
            [new Point2(0, 50), new Point2(6, 50)],
            widthMm: 200);

        var xs = outline.Select(p => p.X).ToList();
        var ys = outline.Select(p => p.Y).ToList();
        Assert.True(ys.Max() - ys.Min() > 190);
        Assert.True(xs.Max() - xs.Min() < 10);
    }

    [Fact]
    public void DisplayOutline_prefers_cad_profile()
    {
        var feature = new PanelFeature
        {
            FeatureId = "G1",
            Kind = "grooveVertical",
            WidthMm = 6,
            Path = [new Point2(0, 0), new Point2(0, 10)],
            Profile =
            [
                new Point2(0, 0),
                new Point2(100, 0),
                new Point2(100, 8),
                new Point2(0, 8),
            ],
        };

        var outline = GrooveGeometry.DisplayOutline(feature);
        Assert.Equal(4, outline.Count);
        Assert.Equal(100, outline.Max(p => p.X), 3);
    }

    [Fact]
    public void Outline_rejects_degenerate_inputs_and_does_not_invent_width()
    {
        Assert.Empty(GrooveGeometry.OutlineFromCenterline(null, 6));
        Assert.Empty(GrooveGeometry.OutlineFromCenterline([new Point2(0, 0)], 6));
        Assert.Empty(GrooveGeometry.OutlineFromCenterline(
            [new Point2(0, 0), new Point2(0, 100)], 0));
        Assert.Equal(0, GrooveGeometry.InferWidthMm([new Point2(0, 0), new Point2(0, 100)]));
    }

    [Fact]
    public void DisplayOutline_uses_explicit_16mm_width()
    {
        var feature = new PanelFeature
        {
            FeatureId = "G1",
            Kind = "grooveVertical",
            WidthMm = 16,
            Path = [new Point2(50, 0), new Point2(50, 200)],
        };
        var outline = GrooveGeometry.DisplayOutline(feature);
        Assert.Equal(4, outline.Count);
        Assert.Equal(16, outline.Max(p => p.X) - outline.Min(p => p.X), 3);
    }
}
