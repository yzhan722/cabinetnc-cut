namespace CabinetNC.Verify;

/// <summary>
/// Shop tolerances the rules judge against. Owned by the verifier — the export
/// pipeline cannot pass looser numbers; tests may construct tighter/looser ones.
/// </summary>
public sealed record VerifyTolerances
{
    /// <summary>Finished-edge position tolerance in XY.</summary>
    public double XyMm { get; init; } = 0.25;
    /// <summary>Cut depth tolerance.</summary>
    public double ZMm { get; init; } = 0.1;
    /// <summary>Smaller leftover / overcut pieces are Clipper noise, not a defect.</summary>
    public double MinAreaMm2 { get; init; } = 4.0;
    /// <summary>Features thinner than this are tessellation ribbons, reported as skipped.</summary>
    public double SliverMm { get; init; } = 0.5;
    /// <summary>Width of the test band just outside/inside a through edge.</summary>
    public double EdgeBandMm { get; init; } = 0.6;
    /// <summary>Drill plunge must land this close to the hole centre.</summary>
    public double DrillCentreMm { get; init; } = 0.5;
    /// <summary>Drill tool Ø vs hole Ø.</summary>
    public double DrillDiameterMm { get; init; } = 0.3;
    /// <summary>A plunge farther than this from a hole is "missing", not "drifted".</summary>
    public double DrillDriftSearchMm { get; init; } = 3.0;
    /// <summary>Groove width measured across the removed region vs CAD width.</summary>
    public double GrooveWidthMm { get; init; } = 0.5;
    /// <summary>Extra slack beyond one tool Ø that outline kerf may extend from a panel.</summary>
    public double OutlineKerfSlackMm { get; init; } = 2.0;
    /// <summary>Surface level: anything cut deeper than this inside a panel must belong to a feature.</summary>
    public double SurfaceDepthMm { get; init; } = 0.3;
    /// <summary>Chord error when tessellating G2/G3 and round offsets.</summary>
    public double ArcChordMm { get; init; } = 0.02;

    public static VerifyTolerances Default { get; } = new();
}
