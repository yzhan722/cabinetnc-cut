namespace CabinetNC.Domain.Manufacturing.Verification;

using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

/// <summary>
/// Where Z=0 sits in the emitted program. Troy posts (<see cref="PostRecipe.Z0IsBoardBottom"/>)
/// put Z0 on the spoilboard so work Z = thickness − depth; the generic post puts Z0 on the
/// board top so work Z = −depth. The verifier must be told — it never guesses.
/// </summary>
public enum ZDatum
{
    BoardTop,
    BoardBottom,
}

/// <summary>One finding of the geometry verifier. <see cref="Code"/> is stable for tests and repair rules.</summary>
public sealed record VerifyIssue(
    string Code,
    string Level,
    string PanelId,
    string? FeatureId,
    double AtX,
    double AtY,
    string Message)
{
    public const string Error = "error";
    public const string Warn = "warn";

    public double? MeasuredMm { get; init; }
    public double? ExpectedMm { get; init; }
    public double? AreaMm2 { get; init; }
    public int SheetIndex { get; init; }

    public bool IsError => Level == Error;
}

public sealed class VerifyReport
{
    public required bool Ok { get; init; }
    public IReadOnlyList<VerifyIssue> Issues { get; init; } = [];
    public int SheetIndex { get; init; }
    public int PanelCount { get; init; }
    public int FeatureCount { get; init; }
    public int StrokeCount { get; init; }
    public string Engine { get; init; } = "";

    public IEnumerable<VerifyIssue> Errors => Issues.Where(i => i.IsError);

    public static VerifyReport Empty(int sheetIndex) => new() { Ok = true, SheetIndex = sheetIndex };

    public static VerifyReport Merge(IEnumerable<VerifyReport> reports)
    {
        var list = reports.ToList();
        var issues = list.SelectMany(r => r.Issues).ToList();
        return new VerifyReport
        {
            Ok = issues.All(i => !i.IsError),
            Issues = issues,
            SheetIndex = list.Count == 1 ? list[0].SheetIndex : -1,
            PanelCount = list.Sum(r => r.PanelCount),
            FeatureCount = list.Sum(r => r.FeatureCount),
            StrokeCount = list.Sum(r => r.StrokeCount),
            Engine = list.Select(r => r.Engine).FirstOrDefault(e => e.Length > 0) ?? "",
        };
    }

    public static string Format(VerifyReport report)
    {
        if (report.Issues.Count == 0) return "计码验算通过";
        return string.Join("\n", report.Issues.Select(i =>
            (i.IsError ? "✗ " : "! ") + $"[{i.Code}] {i.Message}"));
    }
}

/// <summary>
/// One emitted program. <see cref="ToolId"/> is the Sheet×Tool split tool when the
/// dialect carries no <c>M6 T</c> word (generic post writes <c>(tool T3)</c> comments only).
/// </summary>
public sealed record VerifyProgram(string NcText, string? ToolId = null);

/// <summary>
/// Everything the verifier needs for one sheet: CAD intent (panels + placements) and the
/// emitted programs. No <c>CutOp</c>, no planner output — the check must not see the
/// generator's intermediate geometry, otherwise it only re-validates the generator.
/// </summary>
public sealed class VerifyInput
{
    public required IReadOnlyList<Panel> Panels { get; init; }
    public required IReadOnlyList<NestPlacement> Placements { get; init; }
    public int SheetIndex { get; init; }
    /// <summary>All NC programs cut on this sheet (one per tool, or one combined).</summary>
    public required IReadOnlyList<VerifyProgram> Programs { get; init; }
    public required IReadOnlyDictionary<string, ToolDefinition> Tools { get; init; }
    public ZDatum ZDatum { get; init; } = ZDatum.BoardTop;
    /// <summary>Declared tabs. Only these gaps in a through cut are exempt from undercut.</summary>
    public IReadOnlyList<ProfileBridge> Bridges { get; init; } = [];

    public static ZDatum DatumOf(PostRecipe? recipe) =>
        recipe is { Z0IsBoardBottom: true } ? ZDatum.BoardBottom : ZDatum.BoardTop;
}

/// <summary>Export-time geometry gate. Implemented outside Domain (CabinetNC.Verify).</summary>
public interface IExportVerifier
{
    VerifyReport Verify(VerifyInput input);
}
