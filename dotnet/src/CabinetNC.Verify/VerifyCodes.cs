namespace CabinetNC.Verify;

/// <summary>Stable issue codes. Tests and repair rules key on these — rename means a breaking change.</summary>
public static class VerifyCodes
{
    // --- input / coverage (warn unless noted)
    public const string ToolUnknown = "tool_unknown";               // error
    public const string NoPanels = "no_panels";
    public const string PanelUnverifiable = "panel_unverifiable";
    public const string FeatureUnverifiable = "feature_unverifiable";
    public const string FeatureOffPanel = "feature_off_panel";

    // --- through cuts (outline, windows, cutouts, through holes)
    public const string ThroughOvercut = "through_overcut";         // error
    public const string ThroughUndercut = "through_undercut";       // error
    public const string FeatureNotCut = "feature_not_cut";          // error

    // --- blind features (pockets, grooves, cups)
    public const string PocketFloorUncut = "pocket_floor_uncut";    // error
    public const string GrooveFloorUncut = "groove_floor_uncut";    // error
    public const string BlindOvercut = "blind_overcut";             // error
    public const string DepthMismatch = "depth_mismatch";           // error
    public const string GrooveWidthMismatch = "groove_width_mismatch"; // error

    // --- drilling
    public const string HoleDiameterMismatch = "hole_diameter_mismatch"; // error
    public const string DrillCentreDrift = "drill_center_drift";    // error

    // --- sheet
    public const string StrayCut = "stray_cut";                     // warn
}
