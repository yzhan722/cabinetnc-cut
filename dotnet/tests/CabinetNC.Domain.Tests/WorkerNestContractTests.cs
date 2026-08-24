using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

/// <summary>
/// Worker gRPC only sends AABB width/height and forces BLF.
/// These tests lock that contract so Desktop NFP is not claimed as Worker parity.
/// </summary>
public class WorkerNestContractTests
{
    [Fact]
    public void Advanced_stub_does_not_claim_nfp()
    {
        var stub = new AdvancedNestingEngineStub();
        Assert.Equal("advanced_stub_v0", stub.Name);
        var panels = new[]
        {
            new Panel
            {
                PanelId = "A",
                Outline = new Outline { Points = [new(0, 0), new(100, 0), new(100, 80), new(0, 80)] },
            },
        };
        var stock = new[] { new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15 } };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            stub.Pack(panels, new NestSettings(), stock, GroupedBlfNester.SizeOfOutline));
        Assert.Contains("NFP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Worker_shaped_rectangles_route_to_blf_not_nfp()
    {
        var panels = new[]
        {
            new Panel
            {
                PanelId = "A",
                Material = "oak",
                ThicknessMm = 18,
                Outline = new Outline
                {
                    Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
                    Closed = true,
                },
            },
        };
        var (packed, log) = new NestEngineRouter().Run(new NestEngineRequest
        {
            Panels = panels,
            Settings = new NestSettings { MarginMm = 15, ClearanceMm = 12, AllowRotation = true },
            StockTemplates = [new NestSheetSpec { WidthMm = 1220, LengthMm = 2440, BorderMm = 15 }],
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        });
        Assert.Equal("grouped_blf_v0", packed.Engine);
        Assert.Equal("grouped_blf_v0", log.SelectedEngine);
        Assert.DoesNotContain("nfp", packed.Engine, StringComparison.OrdinalIgnoreCase);
    }
}
