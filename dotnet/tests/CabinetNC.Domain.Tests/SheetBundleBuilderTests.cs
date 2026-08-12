using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class SheetBundleBuilderTests
{
    [Fact]
    public void Builds_one_artifact_per_sheet()
    {
        var panels = new[]
        {
            new Panel
            {
                PanelId = "A", ThicknessMm = 18, Material = "oak",
                Outline = new Outline { Points = [new(0, 0), new(100, 0), new(100, 50), new(0, 50)] },
            },
            new Panel
            {
                PanelId = "B", ThicknessMm = 18, Material = "oak",
                Outline = new Outline { Points = [new(0, 0), new(80, 0), new(80, 40), new(0, 40)] },
            },
        };
        var pkg = new CutPackage { SchemaName = CutPackage.Schema, Panels = panels, JobId = "demo" };
        var places = new[]
        {
            new NestPlacement { PanelId = "A", SheetIndex = 0, OffsetX = 10, OffsetY = 10 },
            new NestPlacement { PanelId = "B", SheetIndex = 1, OffsetX = 10, OffsetY = 10 },
        };
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(panels), places);
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, MachineCatalog.Get("nesting_router_6"));
        Assert.Equal(2, bundle.Sheets.Count);
        Assert.Contains(bundle.Sheets[0].ToolPrograms, p => p.NcFileName == "demo_S1_T1.nc");
        Assert.Equal("demo_S2.dxf", bundle.Sheets[1].DxfFileName);
        Assert.Contains("sheet S1", bundle.Sheets[0].ToolPrograms[0].NcText);
        Assert.DoesNotContain("(sheet S2)", bundle.Sheets[0].ToolPrograms[0].NcText);
        Assert.Contains("\"sheetCount\": 2", bundle.RootManifestJson.Replace("\r", ""));
        Assert.Contains("sheet_x_tool_nc", bundle.RootManifestJson);
    }

    [Fact]
    public void Three_sheets_yield_three_nc_files()
    {
        var panels = Enumerable.Range(0, 3).Select(i => new Panel
        {
            PanelId = $"P{i}",
            ThicknessMm = 18,
            Outline = new Outline { Points = [new(0, 0), new(60, 0), new(60, 40), new(0, 40)] },
        }).ToArray();
        var pkg = new CutPackage { SchemaName = CutPackage.Schema, Panels = panels, JobId = "multi" };
        var places = panels.Select((p, i) => new NestPlacement
        {
            PanelId = p.PanelId, SheetIndex = i, OffsetX = 5, OffsetY = 5,
        }).ToList();
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(panels), places);
        var bundle = SheetBundleBuilder.Build(pkg, places, ops, MachineCatalog.Get("nesting_router_6"));
        Assert.Equal(3, bundle.Sheets.Count);
        Assert.Equal(3, bundle.Sheets.Select(s => s.DxfFileName).Distinct().Count());
        Assert.True(bundle.Sheets.SelectMany(s => s.ToolPrograms).Count() >= 3);
        Assert.All(bundle.Sheets, s => Assert.False(string.IsNullOrWhiteSpace(s.DxfText)));
        Assert.All(bundle.Sheets.SelectMany(s => s.ToolPrograms), p => Assert.False(string.IsNullOrWhiteSpace(p.NcText)));
    }

    [Fact]
    public void Fanuc_post_ends_with_M30()
    {
        var post = new FanucLikePostProcessor();
        var nc = post.Emit(
            [
                new CutOp
                {
                    Op = "contour", PanelId = "P", Placed = true, ToolId = "T1",
                    DepthMm = 18.5, Path = [(0, 0), (10, 0), (10, 10), (0, 10)],
                },
            ],
            new MachineProfile
            {
                Id = "fanuc_like_m30",
                Name = "Fanuc-like (M30 end)",
                Dialect = "fanuc_like",
                ProgramEnd = "M30",
            });
        Assert.Contains("M30", nc);
        Assert.Equal("fanuc_like", post.Id);
    }
}
