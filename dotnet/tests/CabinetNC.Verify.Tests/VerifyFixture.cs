using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Manufacturing.Verification;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Verify.Tests;

/// <summary>
/// Test harness only: drives the real generator (plan → nest → offset → emit) the same
/// way the Desktop export does, so the verifier is exercised against shipping NC.
/// </summary>
internal static class VerifyFixture
{
    public const double ToolRadiusMm = 5; // T2 Ø10 — what Desktop ApplyAutomaticToolOffset picks

    public static MachineProfile Profile => MachineCatalog.Get(MachineCatalog.DefaultId);

    public static Panel Rect(string id, double w, double h, double th, params PanelFeature[] features) => new()
    {
        PanelId = id,
        Name = id,
        ThicknessMm = th,
        Outline = new Outline { Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)] },
        Features = features,
    };

    public static PanelFeature Hole(string id, double x, double y, double dia, double? depth = null, bool through = true) => new()
    {
        FeatureId = id,
        Kind = "holeVertical",
        X = x,
        Y = y,
        DiameterMm = dia,
        DepthMm = depth,
        Through = through,
        FaceId = through ? "THROUGH" : "A",
    };

    public static PanelFeature Cutout(string id, double x0, double y0, double x1, double y1) => new()
    {
        FeatureId = id,
        Kind = "cutout",
        Through = true,
        FaceId = "THROUGH",
        Path = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)],
        X = (x0 + x1) / 2,
        Y = (y0 + y1) / 2,
    };

    public static PanelFeature Pocket(string id, double x0, double y0, double x1, double y1, double depth) => new()
    {
        FeatureId = id,
        Kind = "pocket",
        DepthMm = depth,
        FaceId = "A",
        Path = [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)],
        X = (x0 + x1) / 2,
        Y = (y0 + y1) / 2,
    };

    public static PanelFeature Groove(string id, Point2 a, Point2 b, double width, double depth) => new()
    {
        FeatureId = id,
        Kind = "grooveVertical",
        DepthMm = depth,
        WidthMm = width,
        FaceId = "A",
        Path = [a, b],
        X = (a.X + b.X) / 2,
        Y = (a.Y + b.Y) / 2,
    };

    public static NestPlacement Place(string id, double x, double y, double rot = 0, int sheet = 0) => new()
    {
        PanelId = id,
        SheetIndex = sheet,
        OffsetX = x,
        OffsetY = y,
        RotationDeg = rot,
    };

    /// <summary>Desktop export order: FeaturesToOps → AttachToNest → ContourToolOffset.</summary>
    public static IReadOnlyList<CutOp> PlanOps(IReadOnlyList<Panel> panels, IReadOnlyList<NestPlacement> places)
    {
        var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(panels), places);
        return ContourToolOffset.Apply(ops, ToolRadiusMm);
    }

    public static string EmitTroy(IReadOnlyList<CutOp> ops, PostRecipe? recipe = null) =>
        NcEmitter.OpsToNc(ops, Profile, recipe: recipe ?? PostRecipe.TroyDefault());

    public static string EmitGeneric(IReadOnlyList<CutOp> ops) =>
        NcEmitter.OpsToNc(ops, Profile);

    public static VerifyInput Input(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> places,
        string nc,
        ZDatum datum = ZDatum.BoardBottom,
        IReadOnlyList<ProfileBridge>? bridges = null,
        int sheet = 0) => new()
    {
        Panels = panels,
        Placements = places,
        SheetIndex = sheet,
        Programs = [new VerifyProgram(nc)],
        Tools = ToolCatalog.DefaultMap(),
        ZDatum = datum,
        Bridges = bridges ?? [],
    };

    public static VerifyReport Run(VerifyInput input) => new ExportVerifier().Verify(input);

    public static string Describe(VerifyReport r) =>
        $"ok={r.Ok} strokes={r.StrokeCount}\n" + VerifyReport.Format(r);
}
