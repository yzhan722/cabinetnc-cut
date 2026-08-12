namespace CabinetNC.Desktop;

/// <summary>Panel parked in the nest holding bay (待用区), awaiting return to a same-material sheet.</summary>
sealed class HeldNestPart
{
    public required string PanelId { get; init; }
    public required string Material { get; init; }
    public double ThicknessMm { get; init; }
    public double RotationDeg { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
}
