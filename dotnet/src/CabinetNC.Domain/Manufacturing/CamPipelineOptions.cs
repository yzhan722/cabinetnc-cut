namespace CabinetNC.Domain.Manufacturing;

/// <summary>Inputs of the algorithmic part of CAM. Everything interactive (pass toggles, guillotine, bridges) is outside.</summary>
public sealed record CamPipelineOptions(
    bool EnableContour,
    bool EnableDrill,
    bool EnableGroove,
    double ClearanceLargeMinShortMm,
    double DrillMaxExclusiveMm,
    double ContourToolDiameterMm);
