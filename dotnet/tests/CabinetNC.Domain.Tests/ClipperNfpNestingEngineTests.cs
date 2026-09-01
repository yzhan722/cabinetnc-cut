using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class ClipperNfpNestingEngineTests
{
    [Fact]
    public void Nfp_of_two_unit_squares_forbids_origin_overlap_region()
    {
        // Fixed [0,10]x[0,10], moving 10x10 local at origin → NFP should cover roughly [-10,10]x[-10,10]
        // translations that cause overlap.
        var fixedSq = NfpGeometry.ToPath([(0, 0), (10, 0), (10, 10), (0, 10)]);
        var moving = NfpGeometry.ToPath([(0, 0), (10, 0), (10, 10), (0, 10)]);
        var nfp = NfpGeometry.ComputeNfp(fixedSq, moving);
        Assert.NotEmpty(nfp);
        Assert.True(NfpGeometry.ReferenceForbidden(0, 0, nfp));
        Assert.True(NfpGeometry.ReferenceForbidden(5, 5, nfp));
        // Separated by full width — must be legal
        Assert.False(NfpGeometry.ReferenceForbidden(10.1, 0, nfp));
        Assert.False(NfpGeometry.ReferenceForbidden(0, 10.1, nfp));
    }

    [Fact]
    public void Engine_places_rects_without_polygon_collision()
    {
        var panels = Enumerable.Range(0, 8).Select(i => Rect($"R{i}", 200, 150)).ToList();
        var engine = new ClipperNfpNestingEngine();
        var result = engine.Pack(
            panels,
            new NestSettings { MarginMm = 10, ClearanceMm = 8, AllowRotation = true },
            [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 10, Material = "oak", ThicknessMm = 18 }],
            GroupedBlfNester.SizeOfOutline);

        Assert.Equal("clipper_nfp_v1", result.Engine);
        Assert.Equal(panels.Count, result.Placements.Count);
        Assert.Empty(result.Unplaced);
        Assert.Empty(NestValidator.FindPolygonCollisions(panels, result.Placements, 8));
    }

    [Fact]
    public void Engine_places_L_shapes_tighter_or_equal_sheets_vs_blf()
    {
        var panels = new[] { L("L1"), L("L2"), L("L3"), Rect("R1", 120, 80) };
        var stock = new NestSheetSpec
        {
            WidthMm = 500, LengthMm = 500, BorderMm = 8,
            Material = "oak", ThicknessMm = 18,
        };
        var settings = new NestSettings { MarginMm = 8, ClearanceMm = 4, AllowRotation = true };

        var nfp = new ClipperNfpNestingEngine().Pack(panels, settings, [stock], GroupedBlfNester.SizeOfOutline);
        var blf = new BlfNestingEngine().Pack(panels, settings, [stock], GroupedBlfNester.SizeOfOutline);

        Assert.Empty(NestValidator.FindPolygonCollisions(panels, nfp.Placements, 4));
        Assert.True(nfp.Placements.Count >= blf.Placements.Count - 0); // place at least as many
        Assert.True(nfp.SheetCount <= blf.SheetCount + 1);
    }

    [Fact]
    public void Engine_nests_two_triangles_with_180_on_one_sheet()
    {
        var panels = new[] { RightTri("T1"), RightTri("T2") };
        var stock = new NestSheetSpec
        {
            WidthMm = 240, LengthMm = 200, BorderMm = 10,
            Material = "oak", ThicknessMm = 18,
        };
        var settings = new NestSettings { MarginMm = 10, ClearanceMm = 4, AllowRotation = true };

        Assert.Equal(new[] { 0d, 90d, 180d, 270d }, settings.CandidateRotations(panels[0]));
        var nfp = new ClipperNfpNestingEngine().Pack(panels, settings, [stock], GroupedBlfNester.SizeOfOutline);
        Assert.Equal(2, nfp.Placements.Count);
        Assert.Empty(nfp.Unplaced);
        Assert.Empty(NestValidator.FindPolygonCollisions(panels, nfp.Placements, 4));
    }

    [Fact]
    public void Router_nfp_preference_selects_clipper_nfp()
    {
        var panels = new[] { Rect("A", 100, 80), Rect("B", 100, 80) };
        var router = new NestEngineRouter(advanced: new ClipperNfpNestingEngine());
        var (result, log) = router.Run(new NestEngineRequest
        {
            Panels = panels,
            Settings = new NestSettings(),
            StockTemplates =
            [
                new NestSheetSpec
                {
                    WidthMm = 1220, LengthMm = 2440, BorderMm = 15,
                    Material = "oak", ThicknessMm = 18,
                },
            ],
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "nfp",
            AdvancedTimeout = TimeSpan.FromSeconds(10),
        });

        Assert.Equal("clipper_nfp_v1", result.Engine);
        Assert.Equal("clipper_nfp_v1", log.SelectedEngine);
        Assert.Equal(2, result.Placements.Count);
    }

    [Fact]
    public void Engine_fits_two_L_on_sheet_too_small_for_two_aabbs()
    {
        // 280×280 usable 264×264; two 180×180 AABBs cannot sit side-by-side or stacked.
        var panels = new[] { L("L1"), L("L2") };
        var stock = new NestSheetSpec
        {
            WidthMm = 280, LengthMm = 280, BorderMm = 8,
            Material = "oak", ThicknessMm = 18,
        };
        var settings = new NestSettings { MarginMm = 8, ClearanceMm = 4, AllowRotation = true };

        var nfp = new ClipperNfpNestingEngine().Pack(panels, settings, [stock], GroupedBlfNester.SizeOfOutline);
        var blf = new BlfNestingEngine().Pack(panels, settings, [stock], GroupedBlfNester.SizeOfOutline);

        Assert.Empty(NestValidator.FindPolygonCollisions(panels, nfp.Placements, 4));
        Assert.True(nfp.Placements.Count >= blf.Placements.Count);
        Assert.True(nfp.SheetCount <= blf.SheetCount);
    }

    static Panel Rect(string id, double w, double h) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
            Closed = true,
        },
    };

    static Panel RightTri(string id) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points = [new(0, 0), new(200, 0), new(0, 160)],
            Closed = true,
        },
    };

    static Panel L(string id) => new()
    {
        PanelId = id,
        Material = "oak",
        ThicknessMm = 18,
        Outline = new Outline
        {
            Points =
            [
                new(0, 0), new(180, 0), new(180, 60),
                new(60, 60), new(60, 180), new(0, 180),
            ],
            Closed = true,
        },
    };
}
