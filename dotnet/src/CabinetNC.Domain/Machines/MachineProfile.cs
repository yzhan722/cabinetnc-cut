namespace CabinetNC.Domain.Machines;

public sealed class MachineProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Dialect { get; init; } = "generic";
    public string ProgramEnd { get; init; } = "M2";
    public double SafeZMm { get; init; } = 5;
    public double FeedXyMmMin { get; init; } = 3000;
    public double FeedZMmMin { get; init; } = 500;
    public double SpindleRpm { get; init; } = 18000;
    public double ToolDiameterMm { get; init; } = 6;
    public double ContourDepthMm { get; init; } = 18;
    public double ContourStepdownMm { get; init; }
    public double DrillPeckMm { get; init; }
    public bool EnableContour { get; init; } = true;
    public bool EnableDrill { get; init; } = true;
    public bool EnableGroove { get; init; } = true;
    public string? OriginNote { get; init; }
}

public static class MachineCatalog
{
    public const string DefaultId = "osai_e4_1325";

    public static IReadOnlyList<MachineProfile> All { get; } =
    [
        new()
        {
            Id = DefaultId,
            Name = "OSAI E4 1325",
            Dialect = "generic",
            ProgramEnd = "M2",
            SafeZMm = 8,
            FeedXyMmMin = 4000,
            FeedZMmMin = 800,
            SpindleRpm = 18000,
            ToolDiameterMm = 6,
            OriginNote = "1325 nesting table · OSAI E4",
        },
    ];

    public static MachineProfile Get(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? All[0];
}
