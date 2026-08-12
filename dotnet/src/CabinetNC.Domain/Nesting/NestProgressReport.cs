namespace CabinetNC.Domain.Nesting;

/// <summary>UI-facing nest progress (0..Total). Safe to report from background threads.</summary>
public sealed class NestProgressReport
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string Message { get; init; } = "";

    public double Fraction =>
        Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}
