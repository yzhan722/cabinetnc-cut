namespace CabinetNC.Infrastructure.Library;

/// <summary>Workshop library — MakerHub-depth materials / tools / remnants / defaults.</summary>
public sealed class WorkshopLibrary
{
    public const string Schema = "cabinetnc.library";
    public const int SchemaVersion = 1;

    public string SchemaName { get; set; } = Schema;
    public int Version { get; set; } = SchemaVersion;
    public List<LibMaterial> Materials { get; set; } = [];
    public List<LibTool> Tools { get; set; } = [];
    public List<LibRemnant> Remnants { get; set; } = [];
    public NestDefaults Nest { get; set; } = new();
    public LabelerDefaults Labeler { get; set; } = new();
    /// <summary>Most recently opened jobs / projects / machine programs, newest first.</summary>
    public List<RecentFile> RecentFiles { get; set; } = [];
    /// <summary>Canvas display layers (CAD "show/hide"), remembered across sessions.</summary>
    public DisplayLayers Display { get; set; } = new();
    public string? SavedAt { get; set; }
}

public sealed class DisplayLayers
{
    public bool Grain { get; set; } = true;
    public bool Features { get; set; } = true;
    public bool Labels { get; set; } = true;
    public bool Dims { get; set; } = true;
    public bool Rapids { get; set; } = true;
}

public sealed class RecentFile
{
    public string Path { get; set; } = "";
    /// <summary>package | project | anc</summary>
    public string Kind { get; set; } = "package";
    public string? OpenedAt { get; set; }
}

/// <summary>Shop labeler (Excitech Label Printing) settings that the NC export must agree with.</summary>
public sealed class LabelerDefaults
{
    /// <summary>
    /// "Print picture path" configured in the label software on the machine PC. Bitmaps must
    /// sit directly in this folder (no sub-folder); the 2026-08-19 incident was a mismatch here.
    /// </summary>
    public string MachinePictureDir { get; set; } = @"D:\CNC";
}

public sealed class LibMaterial
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double ThicknessMm { get; set; } = 18;
    public string? DensityHint { get; set; }
}

public sealed class LibTool
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MachineId { get; set; }
    public double DiameterMm { get; set; } = 6;
    public double FeedXyMmMin { get; set; } = 3000;
    public double FeedZMmMin { get; set; } = 500;
    public double SpindleRpm { get; set; } = 18000;
}

public sealed class LibRemnant
{
    public string Id { get; set; } = "";
    public string? Material { get; set; }
    public double WidthMm { get; set; }
    public double LengthMm { get; set; }
    public double ThicknessMm { get; set; } = 18;
    public string? Note { get; set; }
    /// <summary>When true, remnant is queued as extra nest stock after primary sheets.</summary>
    public bool UseInNest { get; set; } = true;
}

public sealed class NestDefaults
{
    public double SpacingMm { get; set; } = 12;
    public double BorderMm { get; set; } = 15;
    public bool AllowRotation { get; set; } = true;
    public double DefaultSheetWidthMm { get; set; } = 1200;
    public double DefaultSheetLengthMm { get; set; } = 2400;
}
