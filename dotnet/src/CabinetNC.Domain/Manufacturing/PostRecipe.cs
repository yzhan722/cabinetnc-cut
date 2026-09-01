namespace CabinetNC.Domain.Manufacturing;

/// <summary>
/// Shop post numbers from the stage-4 panes. When passed to <see cref="NcEmitter"/>,
/// Z0 is the board bottom / spoilboard top (Troy), safe Z is 30, and profile is two passes.
/// </summary>
public sealed class PostRecipe
{
    public double SafeZMm { get; init; } = TroyRecipe.SafeZMm;
    public bool Z0IsBoardBottom { get; init; } = true;

    public double TongueFeed { get; init; } = TroyRecipe.TongueFeedMmMin;
    public double TongueRpm { get; init; } = TroyRecipe.SpindleRpm;
    public double TonguePlunge { get; init; } = TroyRecipe.PlungeFeedMmMin;

    public double ClearanceFeed { get; init; } = TroyRecipe.WorkFirstFeedMmMin;
    public double ClearanceRpm { get; init; } = TroyRecipe.SpindleRpm;
    public double ClearancePlunge { get; init; } = TroyRecipe.PlungeFeedMmMin;

    public double ProfileFirstFeed { get; init; } = TroyRecipe.WorkFirstFeedMmMin;
    public double ProfileFirstRpm { get; init; } = TroyRecipe.SpindleRpm;
    public double ProfileFirstPlunge { get; init; } = TroyRecipe.PlungeFeedMmMin;
    public bool ProfileFirstRamp45 { get; init; }
    public double ProfileFirstLeaveMm { get; init; } = TroyRecipe.LastPassLeaveMm;
    public double ProfileBridgeLeaveMm { get; init; } = TroyRecipe.BridgeLeaveMm;

    public double ProfileLastFeed { get; init; } = TroyRecipe.WorkLastFeedMmMin;
    public double ProfileLastRpm { get; init; } = TroyRecipe.SpindleRpm;
    public double ProfileLastPlunge { get; init; } = TroyRecipe.PlungeFeedMmMin;
    public double ProfileThroughZMm { get; init; } = TroyRecipe.ThroughZMm;

    public double DrillPlunge { get; init; } = TroyRecipe.PlungeFeedMmMin;
    public double DrillRpm { get; init; } = TroyRecipe.SpindleRpm;
    public double DrillThroughZMm { get; init; } = TroyRecipe.ThroughZMm;

    public double GuillotineFeed { get; init; } = TroyRecipe.GuillotineFeedMmMin;
    public double GuillotinePlunge { get; init; } = TroyRecipe.GuillotinePlungeMmMin;
    public double GuillotineThroughZMm { get; init; } = TroyRecipe.GuillotineThroughZMm;

    /// <summary>After the last retract, emit <c>G0 X0 Y0</c> before G80. Default on.</summary>
    public bool HomeXyAtEnd { get; init; } = true;

    public IReadOnlyList<ProfileBridge> Bridges { get; init; } = [];

    public static PostRecipe TroyDefault() => new();

    public PostRecipe WithBridges(IReadOnlyList<ProfileBridge> bridges) => new()
    {
        SafeZMm = SafeZMm,
        Z0IsBoardBottom = Z0IsBoardBottom,
        TongueFeed = TongueFeed,
        TongueRpm = TongueRpm,
        TonguePlunge = TonguePlunge,
        ClearanceFeed = ClearanceFeed,
        ClearanceRpm = ClearanceRpm,
        ClearancePlunge = ClearancePlunge,
        ProfileFirstFeed = ProfileFirstFeed,
        ProfileFirstRpm = ProfileFirstRpm,
        ProfileFirstPlunge = ProfileFirstPlunge,
        ProfileFirstRamp45 = ProfileFirstRamp45,
        ProfileFirstLeaveMm = ProfileFirstLeaveMm,
        ProfileBridgeLeaveMm = ProfileBridgeLeaveMm,
        ProfileLastFeed = ProfileLastFeed,
        ProfileLastRpm = ProfileLastRpm,
        ProfileLastPlunge = ProfileLastPlunge,
        ProfileThroughZMm = ProfileThroughZMm,
        DrillPlunge = DrillPlunge,
        DrillRpm = DrillRpm,
        DrillThroughZMm = DrillThroughZMm,
        GuillotineFeed = GuillotineFeed,
        GuillotinePlunge = GuillotinePlunge,
        GuillotineThroughZMm = GuillotineThroughZMm,
        HomeXyAtEnd = HomeXyAtEnd,
        Bridges = bridges,
    };
}
