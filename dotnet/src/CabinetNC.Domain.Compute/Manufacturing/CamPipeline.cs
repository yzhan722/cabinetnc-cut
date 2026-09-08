using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Manufacturing;

/// <summary>
/// The one definition of "features → operations": planner, attach to the nest, contour tool offset.
/// The Desktop's Local mode and the cloud worker both call this, which is what makes their results equal.
/// </summary>
public static class CamPipeline
{
    public static IReadOnlyList<CutOp> Plan(
        IReadOnlyList<Panel> panels,
        IReadOnlyList<NestPlacement> placements,
        CamPipelineOptions options,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var raw = OpsPlanner.FeaturesToOps(
            panels,
            enableContour: options.EnableContour,
            enableDrill: options.EnableDrill,
            enableGroove: options.EnableGroove,
            clearanceLargeMinShortMm: options.ClearanceLargeMinShortMm,
            drillMaxExclusiveMm: options.DrillMaxExclusiveMm);
        ct.ThrowIfCancellationRequested();
        var attached = OpsPlanner.AttachToNest(raw, placements);
        var radius = options.ContourToolDiameterMm / 2;
        return radius < 1e-6 ? attached : ContourToolOffset.Apply(attached, radius);
    }
}
