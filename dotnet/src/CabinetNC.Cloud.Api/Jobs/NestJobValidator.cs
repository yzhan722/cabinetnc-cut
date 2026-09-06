using System.Diagnostics.CodeAnalysis;
using CabinetNC.Cloud.Api.Http;
using CabinetNC.Cloud.Contracts;

namespace CabinetNC.Cloud.Api.Jobs;

/// <summary>API-boundary validation before any object is stored or runner code is reached.</summary>
public static class NestJobValidator
{
    /// <summary>PoC performance target tops out at 500 panels; allow headroom but reject abuse.</summary>
    public const int MaxParts = 1000;
    public const int MaxPanelIdLength = 200;
    public const int MaxMaterialLength = 200;
    public const double MaxDimensionMm = 100_000;

    public static void Validate(SubmitNestJobRequest request)
    {
        if (request.Parts is null || request.Parts.Count is 0 or > MaxParts)
            Invalid($"parts must contain between 1 and {MaxParts} items.");
        if (!PositiveDimension(request.SheetWidthMm) || !PositiveDimension(request.SheetLengthMm))
            Invalid("sheetWidthMm and sheetLengthMm must be finite and positive.");
        if (!NonNegative(request.SpacingMm) || !NonNegative(request.BorderMm))
            Invalid("spacingMm and borderMm must be finite and non-negative.");
        if (request.BorderMm * 2 >= request.SheetWidthMm
            || request.BorderMm * 2 >= request.SheetLengthMm)
        {
            Invalid("borderMm leaves no usable sheet area.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in request.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.PanelId) || part.PanelId.Length > MaxPanelIdLength)
                Invalid($"panelId is required and must be at most {MaxPanelIdLength} characters.");
            if (!ids.Add(part.PanelId))
                Invalid($"panelId '{part.PanelId}' is duplicated.");
            if (!PositiveDimension(part.WidthMm) || !PositiveDimension(part.HeightMm))
                Invalid($"part '{part.PanelId}' dimensions must be finite and positive.");
            if (!NonNegative(part.ThicknessMm))
                Invalid($"part '{part.PanelId}' thicknessMm must be finite and non-negative.");
            if (part.Material?.Length > MaxMaterialLength)
                Invalid($"part '{part.PanelId}' material exceeds {MaxMaterialLength} characters.");
        }
    }

    static bool PositiveDimension(double value) =>
        double.IsFinite(value) && value > 0 && value <= MaxDimensionMm;

    static bool NonNegative(double value) =>
        double.IsFinite(value) && value >= 0 && value <= MaxDimensionMm;

    [DoesNotReturn]
    static void Invalid(string message) =>
        throw new ApiProblemException(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidRequest,
            message);
}
