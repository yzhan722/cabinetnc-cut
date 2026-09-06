using CabinetNC.Compute.Contracts;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using Grpc.Core;

namespace CabinetNC.ComputeWorker.Services;

/// <summary>
/// Thin gRPC adapter: proto request → <see cref="NestingInput"/> → <see cref="INestingRunner"/> → proto reply.
/// All nesting orchestration lives in <c>CabinetNC.Compute.Core</c> so the intranet worker shares it.
/// </summary>
public sealed class NestingServiceImpl(INestingRunner runner) : Nesting.NestingBase
{
    public override Task<StartNestingReply> StartNesting(StartNestingRequest request, ServerCallContext context)
    {
        var output = runner.Run(ToInput(request));
        return Task.FromResult(ToReply(output));
    }

    static NestingInput ToInput(StartNestingRequest request) =>
        new(
            Parts: request.Parts
                .Select(p => new NestingPartInput(p.PanelId, p.WidthMm, p.HeightMm, p.MayRotate, p.Material, p.ThicknessMm))
                .ToList(),
            SheetWidthMm: request.SheetWidthMm,
            SheetLengthMm: request.SheetLengthMm,
            SpacingMm: request.SpacingMm,
            BorderMm: request.BorderMm,
            AllowRotation: request.AllowRotation);

    static StartNestingReply ToReply(NestingOutput output)
    {
        if (!output.Ok)
            return new StartNestingReply { Ok = false, Error = output.Error ?? "" };

        var reply = new StartNestingReply
        {
            Ok = true,
            Engine = output.Engine,
            SheetCount = output.SheetCount,
        };
        reply.Unplaced.AddRange(output.Unplaced);
        foreach (var p in output.Placements)
        {
            reply.Placements.Add(new NestPlacementMsg
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                RotationDeg = p.RotationDeg,
            });
        }
        foreach (var w in output.Warnings)
        {
            reply.Warnings.Add(new NestWarningMsg
            {
                Code = w.Code,
                Message = w.Message,
                PanelIdA = w.PanelIdA ?? "",
                PanelIdB = w.PanelIdB ?? "",
                SheetIndex = w.SheetIndex ?? 0,
            });
        }
        return reply;
    }
}

public sealed class OperationsServiceImpl : Operations.OperationsBase
{
    public override Task<GenerateOperationsReply> GenerateOperations(
        GenerateOperationsRequest request,
        ServerCallContext context)
    {
        try
        {
            var panels = request.Panels.Select(p => new Panel
            {
                PanelId = p.PanelId,
                Material = string.IsNullOrWhiteSpace(p.Material) ? null : p.Material,
                ThicknessMm = p.ThicknessMm > 0 ? p.ThicknessMm : 18,
                Outline = new Outline
                {
                    Points = p.Outline.Select(pt => new Point2(pt.X, pt.Y)).ToList(),
                },
                Features = p.Features.Select(f => new PanelFeature
                {
                    FeatureId = f.FeatureId,
                    Kind = f.Kind,
                    X = f.X,
                    Y = f.Y,
                    DiameterMm = f.DiameterMm,
                    DepthMm = f.DepthMm,
                }).ToList(),
            }).ToList();

            var placements = request.Placements.Select(p => new NestPlacement
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                RotationDeg = p.RotationDeg,
            }).ToList();

            var ops = OpsPlanner.AttachToNest(OpsPlanner.FeaturesToOps(panels), placements);
            var reply = new GenerateOperationsReply
            {
                Ok = true,
                ContourCount = ops.Count(o => o.Op is "contour" or "pocket"),
                DrillCount = ops.Count(o => o.Op == "drill"),
            };
            foreach (var op in ops)
            {
                reply.Ops.Add(new CutOpMsg
                {
                    Op = op.Op,
                    PanelId = op.PanelId,
                    FeatureId = op.FeatureId ?? "",
                    Placed = op.Placed,
                    SheetIndex = op.SheetIndex,
                    SheetX = op.SheetX ?? 0,
                    SheetY = op.SheetY ?? 0,
                    DiameterMm = op.DiameterMm ?? 0,
                    DepthMm = op.DepthMm ?? 0,
                    PathPointCount = op.Path?.Count ?? 0,
                });
            }
            return Task.FromResult(reply);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new GenerateOperationsReply { Ok = false, Error = ex.Message });
        }
    }
}
