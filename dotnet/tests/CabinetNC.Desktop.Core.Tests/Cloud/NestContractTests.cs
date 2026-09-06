using CabinetNC.Cloud.Contracts;
using CabinetNC.Desktop.Core.Cloud;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Desktop.Core.Tests.Cloud;

/// <summary>
/// Intranet mode speaks the rectangular contract of the shared runner. Everything the Desktop's
/// in-process NFP path knows about but the contract cannot carry must be reported, never dropped
/// silently — that list is what the acceptance document has to disclose.
/// </summary>
public class NestRequestBuilderTests
{
    static Panel Rect(string id, double w, double h, string? grain = null, string material = "MDF") => new()
    {
        PanelId = id,
        Material = material,
        ThicknessMm = 18,
        GrainDirection = grain,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
    };

    static Panel LShape(string id) => new()
    {
        PanelId = id,
        Material = "MDF",
        ThicknessMm = 18,
        Outline = new Outline { Points = [new(0, 0), new(600, 0), new(600, 200), new(300, 200), new(300, 400), new(0, 400)] },
    };

    static (double w, double h) SizeOf(Panel p) =>
        (p.Outline.Points.Max(pt => pt.X) - p.Outline.Points.Min(pt => pt.X),
         p.Outline.Points.Max(pt => pt.Y) - p.Outline.Points.Min(pt => pt.Y));

    static readonly NestSettings Settings = new() { MarginMm = 15, ClearanceMm = 12, AllowRotation = true, GrainLock = true };
    static readonly NestSheetSpec Sheet = new() { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12 };

    [Fact]
    public void Rectangular_panels_map_one_to_one_with_no_downgrades()
    {
        var built = NestRequestBuilder.Build([Rect("A", 600, 400), Rect("B", 300, 200)], Settings, [Sheet], SizeOf);

        Assert.Empty(built.Downgrades);
        Assert.Equal(1220, built.Request.SheetWidthMm);
        Assert.Equal(2440, built.Request.SheetLengthMm);
        Assert.Equal(12, built.Request.SpacingMm);
        Assert.Equal(15, built.Request.BorderMm);
        Assert.True(built.Request.AllowRotation);
        Assert.Equal(["A", "B"], built.Request.Parts.Select(p => p.PanelId));
        Assert.Equal((600d, 400d, "MDF", 18d), (built.Request.Parts[0].WidthMm, built.Request.Parts[0].HeightMm, built.Request.Parts[0].Material, built.Request.Parts[0].ThicknessMm));
        Assert.All(built.Request.Parts, p => Assert.True(p.MayRotate));
    }

    [Fact]
    public void Grain_lock_is_applied_per_part_exactly_like_the_local_engine()
    {
        var built = NestRequestBuilder.Build([Rect("free", 600, 400), Rect("grained", 600, 400, grain: "X")], Settings, [Sheet], SizeOf);

        Assert.True(built.Request.Parts[0].MayRotate);
        Assert.False(built.Request.Parts[1].MayRotate);
        Assert.Equal(Settings.PanelMayRotate90(Rect("grained", 600, 400, grain: "X")), built.Request.Parts[1].MayRotate);
    }

    [Fact]
    public void True_shape_outlines_become_bounding_boxes_and_are_disclosed()
    {
        var built = NestRequestBuilder.Build([Rect("A", 600, 400), LShape("L1"), LShape("L2")], Settings, [Sheet], SizeOf);

        var l1 = built.Request.Parts.Single(p => p.PanelId == "L1");
        Assert.Equal((600d, 400d), (l1.WidthMm, l1.HeightMm));
        var downgrade = Assert.Single(built.Downgrades, d => d.Code == "true_shape_to_aabb");
        Assert.Contains("2", downgrade.Message);
    }

    [Fact]
    public void Only_the_first_stock_sheet_is_used_and_extra_sheets_keepouts_and_insets_are_disclosed()
    {
        var remnant = new NestSheetSpec { WidthMm = 600, LengthMm = 900, Label = "remnant" };
        var withKeepout = new NestSheetSpec
        {
            WidthMm = 1220, LengthMm = 2440, BorderMm = 15, InsetLeftMm = 40,
            Blocked = [new NestBlockedRect { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 }],
            AllowPartsInPart = true,
        };

        var built = NestRequestBuilder.Build([Rect("A", 600, 400)], Settings, [withKeepout, remnant], SizeOf);

        Assert.Equal(1220, built.Request.SheetWidthMm);
        Assert.Equal(2440, built.Request.SheetLengthMm);
        var codes = built.Downgrades.Select(d => d.Code).ToList();
        Assert.Contains("extra_sheets_ignored", codes);
        Assert.Contains("keepouts_ignored", codes);
        Assert.Contains("insets_ignored", codes);
        Assert.Contains("parts_in_part_disabled", codes);
    }

    [Fact]
    public void No_stock_sheet_falls_back_to_the_shared_runner_default_and_says_so()
    {
        var built = NestRequestBuilder.Build([Rect("A", 600, 400)], Settings, [], SizeOf);

        Assert.Equal(1220, built.Request.SheetWidthMm);
        Assert.Equal(2440, built.Request.SheetLengthMm);
        Assert.Contains(built.Downgrades, d => d.Code == "default_sheet");
    }

    [Fact]
    public void Mixed_material_stock_sizes_are_disclosed()
    {
        var mdf = new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, Material = "MDF" };
        var ply = new NestSheetSpec { WidthMm = 1250, LengthMm = 2500, Material = "PLY" };

        var built = NestRequestBuilder.Build([Rect("A", 600, 400), Rect("B", 600, 400, material: "PLY")], Settings, [mdf, ply], SizeOf);

        Assert.Contains(built.Downgrades, d => d.Code == "extra_sheets_ignored");
        Assert.Equal(1220, built.Request.SheetWidthMm);
    }
}

public class NestResultMapperTests
{
    static readonly NestSheetSpec Sheet = new() { WidthMm = 1220, LengthMm = 2440, BorderMm = 15, SpacingMm = 12 };

    [Fact]
    public void Server_result_becomes_the_same_shapes_the_desktop_already_renders()
    {
        var jobId = Guid.NewGuid();
        var server = new NestJobResult(
            jobId, "grouped_blf_v0", "CabinetNC.Compute.Core/1.0.0+abc",
            [new NestPlacementDto("A", 0, 15, 15, 0), new NestPlacementDto("B", 1, 627, 15, 90)],
            2, ["BIG"],
            [new NestWarningDto("aabb_gap", "spacing/collision A x B on sheet 0", "A", "B", 0)],
            new string('a', 64), new string('b', 64), 42);

        var (result, log) = NestResultMapper.ToLocal(server, Sheet);

        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal(2, result.SheetCount);
        Assert.Equal(2, result.SheetsUsed.Count);
        Assert.All(result.SheetsUsed, s => Assert.Same(Sheet, s));
        Assert.Equal(["BIG"], result.Unplaced);
        Assert.Equal(("B", 1, 627d, 15d, 90d), (result.Placements[1].PanelId, result.Placements[1].SheetIndex, result.Placements[1].OffsetX, result.Placements[1].OffsetY, result.Placements[1].RotationDeg));
        Assert.Empty(result.PartInPartSlots);
        Assert.Empty(result.GroupReports);

        Assert.Equal("grouped_blf_v0", log.SelectedEngine);
        Assert.Equal("intranet", log.AttemptedEngine);
        Assert.Null(log.FallbackReason);
        Assert.Equal(42, log.ElapsedMs);
    }

    [Fact]
    public void Sheet_count_is_never_below_the_highest_used_sheet()
    {
        var server = new NestJobResult(Guid.NewGuid(), "grouped_blf_v0", "v",
            [new NestPlacementDto("A", 2, 0, 0, 0)], 0, [], [], new string('a', 64), new string('b', 64), 1);

        var (result, _) = NestResultMapper.ToLocal(server, Sheet);

        Assert.Equal(3, result.SheetCount);
        Assert.Equal(3, result.SheetsUsed.Count);
    }
}
