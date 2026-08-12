namespace CabinetNC.Domain.Nesting;

/// <summary>Merge global nest settings with per-stock-kind sheet overrides.</summary>
public static class NestStockOverrides
{
    public static NestSettings ForGroup(NestSettings global, NestSheetSpec stock) =>
        new()
        {
            MarginMm = stock.BorderMm,
            ClearanceMm = stock.SpacingMm,
            AllowRotation = stock.AllowRotation,
            AllowedRotations = global.AllowedRotations,
            RotationStepDeg = global.RotationStepDeg,
            GrainLock = global.GrainLock,
            MirrorPermission = global.MirrorPermission,
            PreferLockedPlacements = global.PreferLockedPlacements,
        };
}
