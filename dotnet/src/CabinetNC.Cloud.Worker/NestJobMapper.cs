using CabinetNC.Cloud.Contracts;
using CabinetNC.Compute.Core.Nesting;

namespace CabinetNC.Cloud.Worker;

/// <summary>Wire contract ↔ runner records. Field for field; no defaults or rounding are introduced here.</summary>
public static class NestJobMapper
{
    public static NestingInput ToNestingInput(SubmitNestJobRequest request) =>
        new(
            request.Parts
                .Select(p => new NestingPartInput(p.PanelId, p.WidthMm, p.HeightMm, p.MayRotate, p.Material, p.ThicknessMm))
                .ToList(),
            request.SheetWidthMm,
            request.SheetLengthMm,
            request.SpacingMm,
            request.BorderMm,
            request.AllowRotation);

    public static NestJobResultPayload ToPayload(NestingOutput output) =>
        new(
            output.Engine,
            output.Placements
                .Select(p => new NestPlacementDto(p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
                .ToList(),
            output.SheetCount,
            output.Unplaced.ToList(),
            output.Warnings
                .Select(w => new NestWarningDto(w.Code, w.Message, w.PanelIdA, w.PanelIdB, w.SheetIndex))
                .ToList());
}
