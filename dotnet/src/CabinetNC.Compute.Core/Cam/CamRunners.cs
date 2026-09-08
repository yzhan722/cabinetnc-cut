using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Domain.Manufacturing;

namespace CabinetNC.Compute.Core.Cam;

/// <summary>CAM: features → operations → attached to the nest → contour tool offset.</summary>
public interface IOperationsRunner
{
    OperationsJobResultPayload Run(SubmitOperationsJobRequest request, CancellationToken ct = default);
}

/// <summary>Post-processor: operations + machine + recipe → NC program text.</summary>
public interface IPostProcessorRunner
{
    PostJobResultPayload Run(SubmitPostJobRequest request, CancellationToken ct = default);
}

/// <summary>
/// The exact chain <c>MainWindow.RebuildOpsOverlay</c> runs before its UI-only steps (pass toggles,
/// guillotine cuts, interactive bridges): <see cref="OpsPlanner.FeaturesToOps"/> →
/// <see cref="OpsPlanner.AttachToNest"/> → <see cref="ContourToolOffset.Apply"/>.
/// </summary>
public sealed class OperationsRunner : IOperationsRunner
{
    public OperationsJobResultPayload Run(SubmitOperationsJobRequest request, CancellationToken ct = default)
    {
        var panels = request.Panels.Select(CamContractMapper.ToPanel).ToList();
        var placements = request.Placements.Select(CamContractMapper.ToPlacement).ToList();
        var ops = RunPipeline(panels, placements, request.Options, ct);
        return CamContractMapper.FromOps(ops);
    }

    /// <summary>Thin adapter over the Domain's <see cref="CamPipeline"/>; the Desktop's Local branch calls the same method.</summary>
    public static IReadOnlyList<CutOp> RunPipeline(
        IReadOnlyList<Domain.Parts.Panel> panels,
        IReadOnlyList<Domain.Nesting.NestPlacement> placements,
        OperationsOptionsDto options,
        CancellationToken ct = default) =>
        CamPipeline.Plan(panels, placements, ToDomain(options), ct);

    public static CamPipelineOptions ToDomain(OperationsOptionsDto o) =>
        new(o.EnableContour, o.EnableDrill, o.EnableGroove, o.ClearanceLargeMinShortMm, o.DrillMaxExclusiveMm, o.ContourToolDiameterMm);

    public static OperationsOptionsDto ToDto(CamPipelineOptions o) =>
        new(o.EnableContour, o.EnableDrill, o.EnableGroove, o.ClearanceLargeMinShortMm, o.DrillMaxExclusiveMm, o.ContourToolDiameterMm);
}

public sealed class PostProcessorRunner : IPostProcessorRunner
{
    public PostJobResultPayload Run(SubmitPostJobRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ops = request.Ops.Select(CamContractMapper.ToOp).ToList();
        var machine = CamContractMapper.ToMachine(request.Machine);
        var recipe = CamContractMapper.ToRecipe(request.Recipe);
        var nc = NcEmitter.OpsToNc(ops, machine, recipe: recipe);
        var lines = nc.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new PostJobResultPayload(nc, lines, machine.Id, machine.Name);
    }
}
