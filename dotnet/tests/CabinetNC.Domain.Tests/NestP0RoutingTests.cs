using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;

namespace CabinetNC.Domain.Tests;

public class NestP0RoutingTests
{
    [Fact]
    public void Grouped_blf_progress_is_monotonic_and_completes_total()
    {
        var progress = new ProgressCollector();
        var panels = new[]
        {
            Rect("A", "oak", 18),
            Rect("B", "oak", 18),
            Rect("C", "mdf", 15),
            Rect("D", "mdf", 15),
        };

        var result = GroupedBlfNester.Pack(
            panels,
            Settings(),
            [Sheet("oak", 18), Sheet("mdf", 15)],
            GroupedBlfNester.SizeOfOutline,
            progress: progress);

        Assert.Equal(panels.Length, result.Placements.Count + result.Unplaced.Count);
        Assert.True(progress.Items.Count >= 4);
        Assert.All(progress.Items, item => Assert.Equal(panels.Length, item.Total));
        Assert.Equal(panels.Length, progress.Items[^1].Done);
        Assert.True(progress.Items.Zip(progress.Items.Skip(1), (a, b) => a.Done <= b.Done).All(x => x));
    }

    [Fact]
    public void Preferred_failure_reports_fallback_and_reason()
    {
        var progress = new ProgressCollector();
        var router = new NestEngineRouter(advanced: new ThrowingEngine(new InvalidOperationException("boom")));

        var (result, log) = router.Run(Request("preferred", progress));

        Assert.Equal("blf_fallback", result.Engine);
        Assert.Equal("blf_fallback", log.SelectedEngine);
        Assert.Equal("throwing", log.AttemptedEngine);
        Assert.Contains("InvalidOperationException", log.FallbackReason);
        Assert.Contains(progress.Items, p => p.Message.Contains("回退"));
    }

    [Fact]
    public void Advanced_timeout_falls_back_and_records_timeout_reason()
    {
        var router = new NestEngineRouter(advanced: new WaitForCancellationEngine());
        var req = Request("nfp");
        req = Copy(req, timeout: TimeSpan.FromMilliseconds(20));

        var (result, log) = router.Run(req);

        Assert.Equal("blf_fallback", result.Engine);
        Assert.Contains("OperationCanceledException", log.FallbackReason);
    }

    [Fact]
    public void Caller_cancellation_before_explicit_blf_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new NestEngineRouter().Run(Request("blf"), cts.Token));
    }

    [Fact]
    public void Caller_cancellation_before_advanced_does_not_return_fallback_result()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var router = new NestEngineRouter(advanced: new WaitForCancellationEngine());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            router.Run(Request("nfp"), cts.Token));
    }

    [Fact]
    public void Unknown_preference_uses_blf_and_records_reason()
    {
        var (result, log) = new NestEngineRouter().Run(Request("not-a-real-engine"));

        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.Equal("grouped_blf_v0", log.SelectedEngine);
        Assert.Equal("not-a-real-engine", log.AttemptedEngine);
        Assert.Equal("unknown_preference", log.FallbackReason);
    }

    [Fact]
    public void Router_overwrites_untrusted_engine_tag_with_selected_engine_name()
    {
        var router = new NestEngineRouter(advanced: new ReturningEngine("reported-by-engine"));

        var (result, log) = router.Run(Request("nfp"));

        Assert.Equal("returning", result.Engine);
        Assert.Equal("returning", log.SelectedEngine);
    }

    [Fact]
    public void Pip_post_pass_preserves_selected_engine_tag()
    {
        var host = HostWithCutout();
        var child = Rect("CHILD", "oak", 18, 60, 40);
        var req = new NestEngineRequest
        {
            Panels = [host, child],
            Settings = Settings(),
            StockTemplates =
            [
                new NestSheetSpec
                {
                    WidthMm = 430,
                    LengthMm = 330,
                    BorderMm = 10,
                    SpacingMm = 8,
                    Material = "oak",
                    ThicknessMm = 18,
                    AllowPartsInPart = true,
                },
            ],
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = "blf",
        };

        var (result, log) = new NestEngineRouter().Run(req);

        Assert.Equal(log.SelectedEngine, result.Engine);
        Assert.Equal("grouped_blf_v0", result.Engine);
        Assert.NotEmpty(result.PartInPartSlots);
    }

    static NestEngineRequest Request(string preference, IProgress<NestProgressReport>? progress = null) =>
        new()
        {
            Panels = [Rect("A"), Rect("B")],
            Settings = Settings(),
            StockTemplates = [Sheet("oak", 18)],
            SizeOf = GroupedBlfNester.SizeOfOutline,
            EnginePreference = preference,
            AdvancedTimeout = TimeSpan.FromSeconds(1),
            Progress = progress,
        };

    static NestEngineRequest Copy(NestEngineRequest source, TimeSpan timeout) =>
        new()
        {
            Panels = source.Panels,
            Settings = source.Settings,
            StockTemplates = source.StockTemplates,
            SizeOf = source.SizeOf,
            EnginePreference = source.EnginePreference,
            AdvancedTimeout = timeout,
            Progress = source.Progress,
        };

    static NestSettings Settings() =>
        new() { MarginMm = 10, ClearanceMm = 8, AllowRotation = true };

    static NestSheetSpec Sheet(string material, double thickness) =>
        new()
        {
            WidthMm = 1220,
            LengthMm = 2440,
            BorderMm = 10,
            SpacingMm = 8,
            Material = material,
            ThicknessMm = thickness,
        };

    static Panel Rect(
        string id,
        string material = "oak",
        double thickness = 18,
        double w = 100,
        double h = 80) =>
        new()
        {
            PanelId = id,
            Material = material,
            ThicknessMm = thickness,
            Outline = new Outline
            {
                Points = [new(0, 0), new(w, 0), new(w, h), new(0, h)],
                Closed = true,
            },
        };

    static Panel HostWithCutout() =>
        new()
        {
            PanelId = "HOST",
            Material = "oak",
            ThicknessMm = 18,
            Outline = new Outline
            {
                Points = [new(0, 0), new(400, 0), new(400, 300), new(0, 300)],
                Closed = true,
            },
            Features =
            [
                new PanelFeature
                {
                    FeatureId = "CUT",
                    Kind = "throughCutout",
                    Through = true,
                    Path = [new(80, 60), new(320, 60), new(320, 240), new(80, 240)],
                },
            ],
        };

    sealed class ProgressCollector : IProgress<NestProgressReport>
    {
        public List<NestProgressReport> Items { get; } = [];

        public void Report(NestProgressReport value) => Items.Add(value);
    }

    sealed class ThrowingEngine(Exception exception) : INestingEngine
    {
        public string Name => "throwing";

        public NestResult Pack(
            IReadOnlyList<Panel> panels,
            NestSettings settings,
            IReadOnlyList<NestSheetSpec> stockTemplates,
            Func<Panel, (double w, double h)> sizeOf,
            CancellationToken ct = default,
            IProgress<NestProgressReport>? progress = null) =>
            throw exception;
    }

    sealed class WaitForCancellationEngine : INestingEngine
    {
        public string Name => "wait-cancel";

        public NestResult Pack(
            IReadOnlyList<Panel> panels,
            NestSettings settings,
            IReadOnlyList<NestSheetSpec> stockTemplates,
            Func<Panel, (double w, double h)> sizeOf,
            CancellationToken ct = default,
            IProgress<NestProgressReport>? progress = null)
        {
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("cancellation was not observed");
        }
    }

    sealed class ReturningEngine(string reportedEngine) : INestingEngine
    {
        public string Name => "returning";

        public NestResult Pack(
            IReadOnlyList<Panel> panels,
            NestSettings settings,
            IReadOnlyList<NestSheetSpec> stockTemplates,
            Func<Panel, (double w, double h)> sizeOf,
            CancellationToken ct = default,
            IProgress<NestProgressReport>? progress = null) =>
            new()
            {
                Engine = reportedEngine,
                Placements = panels.Select((p, i) => new NestPlacement
                {
                    PanelId = p.PanelId,
                    SheetIndex = 0,
                    OffsetX = 10 + i * 120,
                    OffsetY = 10,
                }).ToList(),
                SheetCount = 1,
                SheetsUsed = stockTemplates,
            };
    }
}
