using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.NestContract;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Compute.Core.Nesting;

/// <summary>
/// Rectangular (AABB) nesting orchestration lifted verbatim from the local gRPC worker's
/// <c>NestingServiceImpl.StartNesting</c>. Algorithms stay in <c>CabinetNC.Domain</c>
/// (<see cref="NestEngineRouter"/> → <see cref="GroupedBlfNester"/>, <see cref="NestValidator"/>);
/// this class only builds the request, forces the BLF engine and shapes the result.
/// </summary>
public sealed class NestingRunner : INestingRunner
{
    public const double DefaultBorderMm = 15;
    public const double DefaultSpacingMm = 12;
    public const double DefaultSheetWidthMm = 1220;
    public const double DefaultSheetLengthMm = 2440;
    public const string EnginePreference = "blf";
    public const string StockLabel = "STOCK";

    public NestingOutput Run(NestingInput input)
    {
        try
        {
            var border = input.BorderMm > 0 ? input.BorderMm : DefaultBorderMm;
            var spacing = input.SpacingMm > 0 ? input.SpacingMm : DefaultSpacingMm;
            var sheetW = input.SheetWidthMm > 0 ? input.SheetWidthMm : DefaultSheetWidthMm;
            var sheetL = input.SheetLengthMm > 0 ? input.SheetLengthMm : DefaultSheetLengthMm;

            // Reconstruct panels so every transport uses the same GroupedBlf path as Desktop (Day 13).
            var panels = input.Parts.Select(p => new Panel
            {
                PanelId = p.PanelId,
                Material = string.IsNullOrWhiteSpace(p.Material) ? null : p.Material,
                ThicknessMm = p.ThicknessMm > 0 ? p.ThicknessMm : 0,
                AllowedRotations = p.MayRotate ? null : new[] { 0, 180 },
                Outline = new Outline
                {
                    Points =
                    [
                        new(0, 0),
                        new(p.WidthMm, 0),
                        new(p.WidthMm, p.HeightMm),
                        new(0, p.HeightMm),
                    ],
                    Closed = true,
                },
            }).ToList();

            var settings = new NestSettings
            {
                MarginMm = border,
                ClearanceMm = spacing,
                AllowRotation = input.AllowRotation,
                GrainLock = true,
            };
            var stock = new[]
            {
                new NestSheetSpec
                {
                    WidthMm = sheetW,
                    LengthMm = sheetL,
                    BorderMm = border,
                    Material = null,
                    ThicknessMm = 0,
                    Label = StockLabel,
                },
            };

            var (packed, log) = new NestEngineRouter().Run(new NestEngineRequest
            {
                Panels = panels,
                Settings = settings,
                StockTemplates = stock,
                SizeOf = GroupedBlfNester.SizeOfOutline,
                EnginePreference = EnginePreference,
            });

            var aabbParts = panels.Select(p =>
            {
                var (w, h) = GroupedBlfNester.SizeOfOutline(p);
                return new NestPart { PanelId = p.PanelId, WidthMm = w, HeightMm = h };
            }).ToList();
            var collisions = NestValidator.FindAabbCollisions(aabbParts, packed.Placements, spacing);

            var placements = packed.Placements
                .Select(p => new NestingPlacementOutput(p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
                .ToList();

            var warnings = new List<NestingWarningOutput>();
            if (!string.IsNullOrWhiteSpace(log.FallbackReason))
            {
                warnings.Add(new NestingWarningOutput("engine_fallback", log.FallbackReason, null, null, null));
            }
            foreach (var c in collisions)
            {
                warnings.Add(new NestingWarningOutput(
                    "aabb_gap",
                    $"spacing/collision {c.PanelIdA} × {c.PanelIdB} on sheet {c.SheetIndex}",
                    c.PanelIdA,
                    c.PanelIdB,
                    c.SheetIndex));
            }

            return new NestingOutput(
                Ok: true,
                Engine: packed.Engine,
                Placements: placements,
                SheetCount: packed.SheetCount,
                Unplaced: packed.Unplaced.ToList(),
                Warnings: warnings,
                Error: null);
        }
        catch (Exception ex)
        {
            return new NestingOutput(
                Ok: false,
                Engine: "",
                Placements: [],
                SheetCount: 0,
                Unplaced: [],
                Warnings: [],
                Error: ex.Message);
        }
    }

    /// <summary>
    /// Mirrors <c>MainWindow.RunNestAsync</c>'s local branch exactly: same router, same advanced-engine
    /// choice per preference, same AABB size function. Exceptions propagate (the worker classifies them).
    /// </summary>
    public NestJobResultPayloadV2 RunV2(SubmitNestJobRequestV2 request, CancellationToken ct = default)
    {
        var panels = request.Panels.Select(NestContractV2Mapper.ToPanel).ToList();
        var sheets = request.Sheets.Select(NestContractV2Mapper.ToSheet).ToList();
        var settings = NestContractV2Mapper.ToSettings(request.Settings);
        var (result, log) = RunRouter(panels, settings, sheets, request.EnginePreference, TimeSpan.FromSeconds(request.AdvancedTimeoutSeconds), ct);
        return NestContractV2Mapper.FromResult(result, log);
    }

    /// <summary>The one engine-selection rule shared by the Desktop's local path and the cloud worker.</summary>
    public static (NestResult Result, NestEngineRunLog Log) RunRouter(
        IReadOnlyList<Panel> panels,
        NestSettings settings,
        IReadOnlyList<NestSheetSpec> sheets,
        string enginePreference,
        TimeSpan advancedTimeout,
        CancellationToken ct = default,
        IProgress<NestProgressReport>? progress = null)
    {
        var preference = (enginePreference ?? "preferred").Trim().ToLowerInvariant();
        INestingEngine advanced = preference is "deepnest" or "deepnest_next"
            ? new DeepnestPreviewNestingEngine()
            : new ClipperNfpNestingEngine();
        return new NestEngineRouter(advanced: advanced).Run(
            new NestEngineRequest
            {
                Panels = panels,
                Settings = settings,
                StockTemplates = sheets,
                SizeOf = NestContractV2Mapper.AabbSizeOf,
                EnginePreference = preference,
                AdvancedTimeout = advancedTimeout,
                Progress = progress,
            },
            ct);
    }
}
