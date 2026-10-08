using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestGateDistanceTests
{
    static Panel Rect(string id, double w, double h) => new()
    {
        PanelId = id,
        Material = "m",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
    };

    static IReadOnlyList<NestCollision> Check(double bx, double by) =>
        NestValidator.FindPolygonCollisions(
            [Rect("A", 100, 100), Rect("B", 100, 100)],
            [
                new NestPlacement { PanelId = "A", OffsetX = 0, OffsetY = 0 },
                new NestPlacement { PanelId = "B", OffsetX = bx, OffsetY = by },
            ],
            11.5);

    [Fact]
    public void Diagonal_corners_are_measured_by_true_distance()
    {
        // 9 mm in X and in Y = 12.7 mm apart: legal at 11.5 mm spacing.
        Assert.Empty(Check(109, 109));
        // 7 mm in X and in Y = 9.9 mm apart: too close.
        Assert.Single(Check(107, 107));
    }

    [Fact]
    public void Side_by_side_gap_below_spacing_is_flagged()
    {
        Assert.Single(Check(111, 0));
        Assert.Empty(Check(112, 0));
    }
}
