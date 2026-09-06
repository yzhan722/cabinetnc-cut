using CabinetNC.Compute.Core.Nesting;

namespace CabinetNC.Compute.Core.Tests;

/// <summary>
/// Pins the orchestration lifted out of the local gRPC worker (NestingServiceImpl): rectangular
/// parts, grouped BLF, legacy defaults 1220 × 2440 / spacing 12 / border 15, AABB gap warnings.
/// </summary>
public class NestingRunnerTests
{
    readonly INestingRunner _runner = new NestingRunner();

    static NestingPartInput Part(
        string id,
        double w,
        double h,
        bool mayRotate = true,
        string? material = null,
        double thickness = 0) =>
        new(id, w, h, mayRotate, material, thickness);

    static NestingInput Input(
        IReadOnlyList<NestingPartInput> parts,
        double sheetW = 1220,
        double sheetL = 2440,
        double spacing = 12,
        double border = 15,
        bool allowRotation = true) =>
        new(parts, sheetW, sheetL, spacing, border, allowRotation);

    static (double minX, double minY, double maxX, double maxY) Aabb(NestingPartInput part, NestingPlacementOutput place)
    {
        var turned = Math.Abs(place.RotationDeg % 180 - 90) < 1e-6 || Math.Abs(place.RotationDeg % 180 + 90) < 1e-6;
        var w = turned ? part.HeightMm : part.WidthMm;
        var h = turned ? part.WidthMm : part.HeightMm;
        return (place.OffsetX, place.OffsetY, place.OffsetX + w, place.OffsetY + h);
    }

    [Fact]
    public void Run_places_two_rectangles_without_overlap()
    {
        var input = Input([Part("A", 600, 400), Part("B", 600, 400)]);

        var result = _runner.Run(input);

        Assert.True(result.Ok, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal(2, result.Placements.Count);
        Assert.Empty(result.Unplaced);
        Assert.Equal(1, result.SheetCount);
        Assert.All(result.Placements, p => Assert.Equal(0, p.SheetIndex));
        Assert.DoesNotContain(result.Warnings, w => w.Code == "aabb_gap");

        var a = Aabb(input.Parts[0], result.Placements.Single(p => p.PanelId == "A"));
        var b = Aabb(input.Parts[1], result.Placements.Single(p => p.PanelId == "B"));
        var separated = a.maxX + input.SpacingMm <= b.minX + 1e-6
                        || b.maxX + input.SpacingMm <= a.minX + 1e-6
                        || a.maxY + input.SpacingMm <= b.minY + 1e-6
                        || b.maxY + input.SpacingMm <= a.minY + 1e-6;
        Assert.True(separated, $"A={a} B={b} must be at least {input.SpacingMm} mm apart");

        foreach (var box in new[] { a, b })
        {
            Assert.True(box.minX >= input.BorderMm - 1e-6, $"left border violated: {box}");
            Assert.True(box.minY >= input.BorderMm - 1e-6, $"bottom border violated: {box}");
            Assert.True(box.maxX <= input.SheetWidthMm - input.BorderMm + 1e-6, $"right border violated: {box}");
            Assert.True(box.maxY <= input.SheetLengthMm - input.BorderMm + 1e-6, $"top border violated: {box}");
        }
    }

    [Fact]
    public void Run_honors_no_rotation_part()
    {
        // Sheet 300 × 1200 with border 15 leaves 270 × 1170; a 1000 × 100 part only fits turned 90°.
        var rotatable = _runner.Run(Input([Part("R", 1000, 100, mayRotate: true)], sheetW: 300, sheetL: 1200));
        Assert.True(rotatable.Ok, rotatable.Error);
        var placed = Assert.Single(rotatable.Placements);
        Assert.Equal("R", placed.PanelId);
        Assert.Equal(90d, placed.RotationDeg);
        Assert.Empty(rotatable.Unplaced);

        var locked = _runner.Run(Input([Part("R", 1000, 100, mayRotate: false)], sheetW: 300, sheetL: 1200));
        Assert.True(locked.Ok, locked.Error);
        Assert.Empty(locked.Placements);
        Assert.Equal(["R"], locked.Unplaced);
        Assert.Equal(0, locked.SheetCount);
    }

    [Fact]
    public void Run_returns_unplaced_for_oversized_part()
    {
        var result = _runner.Run(Input([Part("BIG", 3000, 3000), Part("OK", 500, 500)]));

        Assert.True(result.Ok, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(["BIG"], result.Unplaced);
        var placed = Assert.Single(result.Placements);
        Assert.Equal("OK", placed.PanelId);
        Assert.Equal(1, result.SheetCount);
    }

    [Fact]
    public void Run_preserves_material_and_thickness_behavior()
    {
        // Identical footprints, three stock kinds: grouped BLF must never mix kinds on one sheet,
        // and the blank STOCK template must be cloned per group rather than rejecting them.
        var result = _runner.Run(Input(
        [
            Part("MDF18-A", 400, 300, material: "MDF", thickness: 18),
            Part("MDF18-B", 400, 300, material: "MDF", thickness: 18),
            Part("PLY12", 400, 300, material: "Plywood", thickness: 12),
            Part("MDF25", 400, 300, material: "MDF", thickness: 25),
        ]));

        Assert.True(result.Ok, result.Error);
        Assert.Empty(result.Unplaced);
        Assert.Equal(4, result.Placements.Count);
        Assert.Equal(3, result.SheetCount);

        var sheetOf = result.Placements.ToDictionary(p => p.PanelId, p => p.SheetIndex);
        Assert.Equal(sheetOf["MDF18-A"], sheetOf["MDF18-B"]);
        Assert.Equal(3, new[] { sheetOf["MDF18-A"], sheetOf["PLY12"], sheetOf["MDF25"] }.Distinct().Count());
    }

    [Fact]
    public void Run_is_deterministic_for_same_input()
    {
        var parts = Enumerable.Range(0, 24)
            .Select(i => Part(
                $"P{i:00}",
                200 + i * 37 % 500,
                150 + i * 53 % 400,
                mayRotate: i % 3 != 0,
                material: i % 2 == 0 ? "MDF" : "PLY",
                thickness: 18))
            .ToList();
        var input = Input(parts);

        var first = _runner.Run(input);
        var second = _runner.Run(input);
        var freshInstance = new NestingRunner().Run(input);

        Assert.True(first.Ok, first.Error);
        Assert.NotEmpty(first.Placements);
        foreach (var other in new[] { second, freshInstance })
        {
            Assert.Equal(first.Ok, other.Ok);
            Assert.Equal(first.Engine, other.Engine);
            Assert.Equal(first.SheetCount, other.SheetCount);
            Assert.Equal(first.Placements, other.Placements);
            Assert.Equal(first.Unplaced, other.Unplaced);
            Assert.Equal(first.Warnings, other.Warnings);
            Assert.Equal(first.Error, other.Error);
        }
    }

    [Fact]
    public void Run_applies_legacy_worker_defaults_for_non_positive_dimensions()
    {
        // 0 for sheet/spacing/border must behave exactly like the gRPC worker: 1220 × 2440 / 12 / 15.
        // FILL is the exact inner rect (1190 × 2410) and lands at the border origin; EDGE is 10 mm too
        // wide for that inner rect and cannot turn, so it must stay unplaced.
        var result = _runner.Run(new NestingInput(
            [Part("FILL", 1190, 2410, mayRotate: false), Part("EDGE", 1200, 2410, mayRotate: false)],
            SheetWidthMm: 0,
            SheetLengthMm: 0,
            SpacingMm: 0,
            BorderMm: 0,
            AllowRotation: true));

        Assert.True(result.Ok, result.Error);
        var fill = Assert.Single(result.Placements);
        Assert.Equal("FILL", fill.PanelId);
        Assert.Equal(15d, fill.OffsetX, 6);
        Assert.Equal(15d, fill.OffsetY, 6);
        Assert.Equal(0d, fill.RotationDeg);
        Assert.Equal(["EDGE"], result.Unplaced);
        Assert.Equal(1, result.SheetCount);
    }

    [Fact]
    public void Run_returns_error_instead_of_throwing()
    {
        // Duplicate panel ids blow up inside the AABB validator; the worker surfaced that as
        // Ok=false + message rather than a faulted call, and the runner must keep doing so.
        var result = _runner.Run(Input([Part("DUP", 400, 300), Part("DUP", 400, 300)]));

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Placements);
        Assert.Empty(result.Unplaced);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.SheetCount);
    }
}
