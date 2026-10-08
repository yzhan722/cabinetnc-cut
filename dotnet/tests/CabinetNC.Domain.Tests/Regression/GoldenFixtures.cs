using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests.Regression;

public sealed class GoldenJob
{
    public required string Id { get; init; }
    public required string Post { get; init; }
    public required IReadOnlyList<Panel> Panels { get; init; }
}

public static class GoldenFixtures
{
    public static Panel OakHoleGroove(string id, double w, double h) =>
        Rect(id, "oak", 18, w, h, hole: true, groove: true);

    public static Panel Rect(string id, string mat, double th, double w, double h, bool hole = true, bool groove = false) => new()
    {
        PanelId = id,
        Material = mat,
        ThicknessMm = th,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
        Features = BuildFeatures(th, w, h, hole, groove),
        Identity = new WorkpieceIdentity { WorkpieceId = id, ModuleId = "GOLD", ProjectId = "REG" },
    };

    static List<PanelFeature> BuildFeatures(double th, double w, double h, bool hole, bool groove)
    {
        var list = new List<PanelFeature>();
        if (hole)
        {
            list.Add(new PanelFeature
            {
                FeatureId = "H1", Kind = "holeVertical",
                X = w * 0.3, Y = h * 0.3, DiameterMm = 3, DepthMm = Math.Max(1, th - 2),
            });
        }
        if (groove)
        {
            list.Add(new PanelFeature
            {
                FeatureId = "G1", Kind = "grooveVertical",
                DepthMm = Math.Min(6, th - 1),
                Path = [new(10, 10), new(w - 10, 10)],
            });
        }
        return list;
    }

    public static GoldenJob SheetToolSinglePanel() => new()
    {
        Id = "sheet_tool_single_panel",
        Post = "sheet_tool",
        Panels = [OakHoleGroove("P", 200, 150)],
    };

    public static GoldenJob MultiMaterialNoShare() => new()
    {
        Id = "multi_material_no_share",
        Post = "sheet_tool",
        Panels =
        [
            Rect("A", "oak", 18, 400, 300, hole: true, groove: false),
            Rect("B", "mdf", 18, 350, 280, hole: true, groove: false),
        ],
    };

    public static GoldenJob TroySingleFileAtc() => new()
    {
        Id = "troy_single_file_atc",
        Post = "troy",
        Panels = [OakHoleGroove("P", 200, 150)],
    };
}
