using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using CabinetNC.Infrastructure.Diagnostics;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace CabinetNC.Desktop;

enum PanelDraftMode
{
    Profile,
    Feature,
    Guide,
}

readonly record struct DraftChain(
    PanelDraftMode Mode,
    IReadOnlyList<WorldPt> Pts,
    bool IsCircle = false,
    WorldPt Center = default,
    double Radius = 0,
    double? DepthMm = null,
    double? WidthMm = null);

enum DraftTool
{
    None,
    Line,
    Rect,
    Circle,
}

enum LinePhase
{
    Idle,
    WaitFirst,
    WaitNext,
}

enum SnapKind
{
    Origin,
    End,
    Mid,
    Close,
    Center,
}

enum DynField
{
    None,
    X,
    Y,
}

public sealed record DraftStockKind(string Material, double ThicknessMm, string Label);

public sealed class RemnantDraftResult
{
    public required IReadOnlyList<Point2> Outline { get; init; }
    public double WidthMm { get; init; }
    public double LengthMm { get; init; }
    public string? Material { get; init; }
    public double ThicknessMm { get; init; }
    public string? Note { get; init; }
}

readonly record struct WorldPt(double X, double Y);
readonly record struct SnapHit(WorldPt Pt, SnapKind Kind);
readonly record struct DraftView(float Ox, float Oy, float Scale, int W, int H);

public partial class PanelDraftWindow : Window
{
    const float OriginInset = 56;
    const float MinorMm = 50;
    const float OsnapPx = 12;

    PanelDraftMode _mode = PanelDraftMode.Profile;
    DraftTool _tool = DraftTool.None;
    LinePhase _phase = LinePhase.Idle;
    bool _snapOn = true;
    bool _orthoOn;
    DraftView _view = new(OriginInset, OriginInset, 1.5f, 1, 1);
    float _scale = 1.5f;
    float _ox = OriginInset;
    float _oy = OriginInset;
    bool _viewReady;
    bool _panning;
    (float X, float Y) _panLast;
    double _dpiX = 1, _dpiY = 1;

    readonly List<DraftChain> _chains = [];
    readonly List<WorldPt> _current = [];
    WorldPt? _cursor;
    SnapHit? _hoverSnap;
    double? _lockDx;
    double? _lockDy;
    DynField _dynEdit = DynField.None;
    bool _dynTyped;
    bool _syncingDim;
    bool _rectFromCenter;
    bool _circleDiameter;
    Panel? _seed;
    bool _editMode;
    bool _remnantMode;
    DraftChain? _pendingFeature;
    bool _waitDepth;
    double? _lastFeatureDepth;
    double? _lastGrooveWidth = 6;

    public Panel? ResultPanel { get; private set; }
    public RemnantDraftResult? ResultRemnant { get; private set; }
    public bool Confirmed { get; private set; }

    public PanelDraftWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => DraftHost.InvalidateVisual();
        Closing += (_, e) =>
        {
            if (!EditConfirmDiscard())
                e.Cancel = true;
        };
        Loaded += (_, _) =>
        {
            RefreshDpi();
            _viewReady = false;
            ApplyMode(PanelDraftMode.Profile);
            RefreshPrompt();
            CommandBox.Focus();
        };
    }

    public void PrepareCreate(string panelId, string? name, string? material, double thicknessMm, Panel? seed = null)
    {
        _editMode = false;
        _seed = seed;
        Title = "创建板件";
        if (CommitBtn is not null) CommitBtn.Content = "加入方案";
        DraftNameBox.Text = string.IsNullOrWhiteSpace(name) ? panelId : name;
        DraftIdBox.Text = panelId;
        SelectKind(material, thicknessMm);
        UsageLog.LogEvent("ui", "draft.open", new Dictionary<string, object?>
        {
            ["mode"] = "create",
            ["panelId"] = panelId,
            ["material"] = material,
            ["thicknessMm"] = thicknessMm,
        });
    }

    public void LockKind()
    {
        if (DraftKindCombo is null) return;
        DraftKindCombo.IsEnabled = false;
        DraftKindCombo.ToolTip = "密排创建：材料跟当前大板，不可改";
    }

    /// <summary>
    /// 画余料: same canvas as 创建板件, Profile only. Result is an ortho outline, not a panel.
    /// </summary>
    public void PrepareRemnant(
        string? remnantId,
        string? note,
        string? material,
        double thicknessMm,
        IReadOnlyList<Point2>? outline)
    {
        _remnantMode = true;
        _editMode = false;
        _seed = null;
        Title = string.IsNullOrWhiteSpace(remnantId) ? "画余料" : "编辑余料";
        if (CommitBtn is not null)
            CommitBtn.Content = string.IsNullOrWhiteSpace(remnantId) ? "加入余料" : "写回余料";
        if (DraftIdLabel is not null) DraftIdLabel.Visibility = Visibility.Collapsed;
        DraftIdBox.Visibility = Visibility.Collapsed;
        DraftIdBox.Text = remnantId ?? "";
        DraftNameBox.Text = note ?? "";
        if (ModeFeature is not null) ModeFeature.Visibility = Visibility.Collapsed;
        if (ModeGuide is not null) ModeGuide.Visibility = Visibility.Collapsed;
        if (ToolCircle is not null) ToolCircle.Visibility = Visibility.Collapsed;
        SelectKind(material, thicknessMm);
        SetOrtho(true);
        ApplyMode(PanelDraftMode.Profile);
        _chains.Clear();
        _current.Clear();
        var seed = outline is { Count: >= 4 }
            ? outline
            : null;
        if (seed is not null)
        {
            var pts = seed.Select(p => new WorldPt(p.X, p.Y)).ToList();
            if (pts.Count >= 2 && !NearPt(pts[0], pts[^1]))
                pts.Add(pts[0]);
            _chains.Add(new DraftChain(PanelDraftMode.Profile, pts));
        }

        UsageLog.LogEvent("ui", "draft.open", new Dictionary<string, object?>
        {
            ["mode"] = string.IsNullOrWhiteSpace(remnantId) ? "remnant" : "remnantEdit",
            ["remnantId"] = remnantId,
            ["material"] = material,
            ["thicknessMm"] = thicknessMm,
            ["vertexCount"] = seed?.Count ?? 0,
        });
        RefreshPrompt();
        Redraw();
    }

    public void PrepareCreateFrom(Panel panel)
    {
        _editMode = true;
        _seed = panel;
        Title = "创建板件";
        if (CommitBtn is not null) CommitBtn.Content = "加入方案";
        DraftNameBox.Text = panel.Name ?? panel.DisplayTitle;
        DraftIdBox.Text = panel.PanelId;
        _chains.Clear();
        _current.Clear();
        foreach (var fig in PanelDraftCompile.Explode(panel))
            _chains.Add(FromFigure(fig));
        UsageLog.LogEvent("ui", "draft.open", new Dictionary<string, object?>
        {
            ["mode"] = "createFrom",
            ["panelId"] = panel.PanelId,
            ["name"] = panel.DisplayTitle,
            ["material"] = panel.Material,
            ["thicknessMm"] = panel.ThicknessMm,
            ["featureCount"] = panel.Features.Count,
            ["chainCount"] = _chains.Count,
        });
        RefreshPrompt();
        Redraw();
    }

    public void SetStockKinds(IReadOnlyList<DraftStockKind> kinds, string? material, double thicknessMm)
    {
        var list = kinds.Count > 0
            ? kinds.ToList()
            : FallbackKind(material, thicknessMm);
        DraftKindCombo.ItemsSource = list;
        SelectKind(material, thicknessMm);
    }

    void SelectKind(string? material, double thicknessMm)
    {
        if (DraftKindCombo.ItemsSource is not IEnumerable<DraftStockKind> items)
            return;
        var hit = items.FirstOrDefault(k =>
            string.Equals(k.Material, material ?? "", StringComparison.OrdinalIgnoreCase)
            && Math.Abs(k.ThicknessMm - thicknessMm) < 0.05)
            ?? items.FirstOrDefault();
        DraftKindCombo.SelectedItem = hit;
    }

    static List<DraftStockKind> FallbackKind(string? material, double thicknessMm)
    {
        var thk = thicknessMm > 0 ? thicknessMm : 18;
        var mat = string.IsNullOrWhiteSpace(material) ? "carcass" : material.Trim();
        return [new DraftStockKind(mat, thk, $"{mat} · {thk:0.##}mm")];
    }

    DraftStockKind? SelectedKind() => DraftKindCombo.SelectedItem as DraftStockKind;

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void OnCommitClick(object sender, RoutedEventArgs e)
    {
        if (_editorOn)
        {
            EditCommit();
            return;
        }
        if (_remnantMode)
        {
            CommitRemnant();
            return;
        }
        if (_waitDepth)
        {
            ShowDepthPrompt();
            CommandBox.Focus();
            return;
        }
        if (_mode == PanelDraftMode.Feature && _current.Count >= 2)
        {
            FinishCurrent(commit: true);
            return;
        }
        FinishCurrent(commit: _mode != PanelDraftMode.Feature);
        var figures = _chains
            .Where(c => c.Mode != PanelDraftMode.Guide)
            .Select(ToFigure)
            .ToList();
        var kind = SelectedKind();
        if (kind is null)
        {
            UsageLog.LogEvent("warn", "draft.compile", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["panelId"] = (DraftIdBox.Text ?? "").Trim(),
                ["figureCount"] = figures.Count,
            }, error: "no stock kind");
            CommandPrompt.Text = "命令: 先选择板材种类";
            return;
        }

        var id = (DraftIdBox.Text ?? "").Trim();
        if (id.Length == 0) id = _seed?.PanelId ?? "DRAFT-1";
        var result = PanelDraftCompile.TryBuild(figures, new DraftPanelRequest
        {
            PanelId = id,
            Name = DraftNameBox.Text,
            Material = kind.Material,
            ThicknessMm = kind.ThicknessMm,
            Identity = _seed?.Identity,
            Seed = _seed,
            NormalizeOrigin = !_editMode,
            ModuleId = _seed?.Identity?.ModuleId ?? "Draft",
        });
        if (!result.Ok || result.Panel is null)
        {
            UsageLog.LogEvent("warn", "draft.compile", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["panelId"] = id,
                ["material"] = kind.Material,
                ["thicknessMm"] = kind.ThicknessMm,
                ["figureCount"] = figures.Count,
            }, error: result.Error ?? "无法生成板件");
            CommandPrompt.Text = $"命令: {result.Error ?? "无法生成板件"}";
            return;
        }

        UsageLog.LogEvent("ui", "draft.commit", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["panelId"] = result.Panel.PanelId,
            ["name"] = result.Panel.DisplayTitle,
            ["material"] = result.Panel.Material,
            ["thicknessMm"] = result.Panel.ThicknessMm,
            ["featureCount"] = result.Panel.Features.Count,
            ["figureCount"] = figures.Count,
            ["editMode"] = _editMode,
        });
        ResultPanel = result.Panel;
        Confirmed = true;
        DialogResult = true;
    }

    void CommitRemnant()
    {
        FinishCurrent(commit: true);
        var kind = SelectedKind();
        if (kind is null)
        {
            CommandPrompt.Text = "命令: 先选择板材种类";
            return;
        }

        var figs = _chains
            .Where(c => c.Mode == PanelDraftMode.Profile)
            .Select(ToFigure)
            .Where(f => !f.IsCircle && PanelDraftCompile.CanBeOutline(f))
            .ToList();
        if (figs.Count == 0)
        {
            CommandPrompt.Text = "命令: 先画闭合的直角外框（LINE / REC）";
            return;
        }

        var fig = figs[^1];
        if (!RemnantOutline.TryBuild(fig.Points, out var shape, out var err) || shape is null)
        {
            UsageLog.LogEvent("warn", "draft.compile", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["mode"] = "remnant",
                ["figureCount"] = figs.Count,
            }, error: err ?? "无法生成余料");
            CommandPrompt.Text = $"命令: {err ?? "无法生成余料"}";
            return;
        }

        var note = (DraftNameBox.Text ?? "").Trim();
        ResultRemnant = new RemnantDraftResult
        {
            Outline = shape.Outline,
            WidthMm = shape.WidthMm,
            LengthMm = shape.LengthMm,
            Material = string.IsNullOrWhiteSpace(kind.Material) ? null : kind.Material,
            ThicknessMm = kind.ThicknessMm,
            Note = note.Length == 0 ? null : note,
        };
        UsageLog.LogEvent("ui", "draft.commit", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["mode"] = "remnant",
            ["material"] = ResultRemnant.Material,
            ["thicknessMm"] = ResultRemnant.ThicknessMm,
            ["w"] = shape.WidthMm,
            ["l"] = shape.LengthMm,
            ["blocked"] = shape.Blocked.Count,
            ["vertexCount"] = shape.Outline.Count,
        });
        Confirmed = true;
        DialogResult = true;
    }

    static DraftFigure ToFigure(DraftChain chain) =>
        new()
        {
            Layer = chain.Mode switch
            {
                PanelDraftMode.Feature => DraftLayer.Feature,
                PanelDraftMode.Guide => DraftLayer.Guide,
                _ => DraftLayer.Profile,
            },
            Points = chain.Pts.Select(p => new Point2(p.X, p.Y)).ToList(),
            Closed = chain.IsCircle || (chain.Pts.Count >= 3 && NearPt(chain.Pts[0], chain.Pts[^1])),
            IsCircle = chain.IsCircle && chain.Radius > 0.25,
            CenterX = chain.Center.X,
            CenterY = chain.Center.Y,
            RadiusMm = chain.Radius,
            DepthMm = chain.DepthMm,
            WidthMm = chain.WidthMm,
        };

    static DraftChain FromFigure(DraftFigure fig)
    {
        var pts = fig.Points.Select(p => new WorldPt(p.X, p.Y)).ToList();
        var mode = fig.Layer switch
        {
            DraftLayer.Feature => PanelDraftMode.Feature,
            DraftLayer.Guide => PanelDraftMode.Guide,
            _ => PanelDraftMode.Profile,
        };
        return new DraftChain(
            mode, pts, fig.IsCircle, new WorldPt(fig.CenterX, fig.CenterY), fig.RadiusMm,
            fig.DepthMm, fig.WidthMm);
    }

    static bool NearPt(WorldPt a, WorldPt b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    static int UniqueWorldCount(IReadOnlyList<WorldPt> pts)
    {
        var n = 0;
        WorldPt? last = null;
        foreach (var p in pts)
        {
            if (last is { } q && NearPt(q, p)) continue;
            last = p;
            n++;
        }
        if (n >= 2 && NearPt(pts[0], pts[^1])) n--;
        return n;
    }

    static bool IsClosedWorld(IReadOnlyList<WorldPt> pts) =>
        UniqueWorldCount(pts) >= 3 && NearPt(pts[0], pts[^1]);

    bool HasBoardOutline()
    {
        var figs = _chains
            .Where(c => c.Mode == PanelDraftMode.Profile)
            .Select(ToFigure)
            .ToList();
        if (_mode == PanelDraftMode.Profile && UniqueWorldCount(_current) >= 3)
            figs.Add(ToFigure(new DraftChain(_mode, [.. _current])));
        if (_remnantMode)
            return figs.Any(f => !f.IsCircle && PanelDraftCompile.CanBeOutline(f));
        return figs.Any(PanelDraftCompile.CanBeOutline);
    }

    void SyncCommitChrome()
    {
        if (CommitBtn is null || _editorOn) return;
        var ready = HasBoardOutline();
        CommitBtn.IsEnabled = ready;
        CommitBtn.Opacity = ready ? 1 : 0.45;
        CommitBtn.ToolTip = _remnantMode
            ? (ready ? "把当前外框收成余料" : "先画闭合的直角外框")
            : ready
                ? "把当前 Profile 收成板件定义并加入方案"
                : "先画闭合的 Profile 外框";
    }

    void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (_remnantMode)
        {
            ApplyMode(PanelDraftMode.Profile);
            return;
        }
        if (ReferenceEquals(sender, ModeFeature))
            ApplyMode(PanelDraftMode.Feature);
        else if (ReferenceEquals(sender, ModeGuide))
            ApplyMode(PanelDraftMode.Guide);
        else
            ApplyMode(PanelDraftMode.Profile);
    }

    void OnClearAllGuides(object sender, RoutedEventArgs e)
    {
        var n = _chains.RemoveAll(c => c.Mode == PanelDraftMode.Guide);
        if (_mode == PanelDraftMode.Guide)
        {
            _current.Clear();
            _hoverSnap = null;
            if (_tool is DraftTool.Line or DraftTool.Rect or DraftTool.Circle)
                _phase = LinePhase.WaitFirst;
        }
        var msg = n == 0 ? "命令: 没有辅助线" : $"命令: 已删除 {n} 条辅助线";
        if (_editorOn) RefreshPrompt();
        CommandPrompt.Text = msg;
        Redraw();
    }

    void ApplyMode(PanelDraftMode mode)
    {
        if (_remnantMode)
            mode = PanelDraftMode.Profile;
        if (_waitDepth || (_editorOn && _editPhase == EditPhase.LineDepth))
        {
            ModeProfile.IsChecked = _mode == PanelDraftMode.Profile;
            ModeFeature.IsChecked = _mode == PanelDraftMode.Feature;
            ModeGuide.IsChecked = _mode == PanelDraftMode.Guide;
            if (_waitDepth) ShowDepthPrompt();
            else RefreshPrompt();
            CommandBox.Focus();
            return;
        }
        if (!_editorOn && _mode != mode)
            FinishCurrent(commit: _mode != PanelDraftMode.Feature);
        _mode = mode;
        ModeProfile.IsChecked = mode == PanelDraftMode.Profile;
        ModeFeature.IsChecked = mode == PanelDraftMode.Feature;
        ModeGuide.IsChecked = mode == PanelDraftMode.Guide;
        RefreshPrompt();
        Redraw();
        if (_editorOn) CommandBox.Focus();
    }

    void OnToolLineClick(object sender, RoutedEventArgs e)
    {
        if (_tool == DraftTool.Line)
            ExitTool(commit: true);
        else
            StartLine();
    }

    void OnToolRectClick(object sender, RoutedEventArgs e)
    {
        if (_tool == DraftTool.Rect)
            ExitTool(commit: true);
        else
            StartRect();
    }

    void OnRectMenuOpened(object sender, RoutedEventArgs e)
    {
        RectMenuCorner.IsChecked = !_rectFromCenter;
        RectMenuCenter.IsChecked = _rectFromCenter;
    }

    void OnRectCornerMode(object sender, RoutedEventArgs e) => SetRectFromCenter(false);

    void OnRectCenterMode(object sender, RoutedEventArgs e) => SetRectFromCenter(true);

    void OnToolCircleClick(object sender, RoutedEventArgs e)
    {
        if (_tool == DraftTool.Circle)
            ExitTool(commit: true);
        else
            StartCircle();
    }

    void OnCircleMenuOpened(object sender, RoutedEventArgs e)
    {
        CircleMenuCenter.IsChecked = !_circleDiameter;
        CircleMenuDiameter.IsChecked = _circleDiameter;
    }

    void OnCircleCenterMode(object sender, RoutedEventArgs e) => SetCircleDiameter(false);

    void OnCircleDiameterMode(object sender, RoutedEventArgs e) => SetCircleDiameter(true);

    void SetCircleDiameter(bool diameter)
    {
        _circleDiameter = diameter;
        StartCircle();
    }

    void SetRectFromCenter(bool center)
    {
        _rectFromCenter = center;
        StartRect();
    }

    void OnSnapChip(object sender, RoutedEventArgs e) => SetSnap(SnapChip.IsChecked == true);

    void OnOrthoChip(object sender, RoutedEventArgs e) => SetOrtho(OrthoChip.IsChecked == true);

    void SetSnap(bool on)
    {
        _snapOn = on;
        SnapChip.IsChecked = on;
        Redraw();
    }

    void SetOrtho(bool on)
    {
        _orthoOn = on;
        OrthoChip.IsChecked = on;
        if (_cursor is { } cur)
            _hoverSnap = ResolvePoint(cur, updateHover: true).Snap;
        Redraw();
    }

    void StartLine()
    {
        if (_waitDepth) { ShowDepthPrompt(); CommandBox.Focus(); return; }
        FinishCurrent(commit: _mode != PanelDraftMode.Feature);
        _tool = DraftTool.Line;
        _phase = LinePhase.WaitFirst;
        _current.Clear();
        SyncToolButtons();
        CommandBox.Clear();
        RefreshPrompt();
        CommandBox.Focus();
        Redraw();
    }

    void StartRect()
    {
        if (_waitDepth) { ShowDepthPrompt(); CommandBox.Focus(); return; }
        FinishCurrent(commit: _mode != PanelDraftMode.Feature);
        ClearDyn();
        _tool = DraftTool.Rect;
        _phase = LinePhase.WaitFirst;
        _current.Clear();
        SyncToolButtons();
        CommandBox.Clear();
        RefreshPrompt();
        CommandBox.Focus();
        Redraw();
    }

    void StartCircle()
    {
        if (_remnantMode)
        {
            CommandPrompt.Text = "命令: 余料只画直角外框（LINE / REC）";
            CommandBox.Focus();
            return;
        }
        if (_waitDepth) { ShowDepthPrompt(); CommandBox.Focus(); return; }
        FinishCurrent(commit: _mode != PanelDraftMode.Feature);
        ClearDyn();
        _tool = DraftTool.Circle;
        _phase = LinePhase.WaitFirst;
        _current.Clear();
        SyncToolButtons();
        CommandBox.Clear();
        RefreshPrompt();
        CommandBox.Focus();
        Redraw();
    }

    void ClearDyn()
    {
        _lockDx = null;
        _lockDy = null;
        _dynEdit = DynField.None;
        _dynTyped = false;
    }

    bool DynActive =>
        _phase == LinePhase.WaitNext && _current.Count > 0
        && _tool is DraftTool.Line or DraftTool.Rect or DraftTool.Circle;

    WorldPt DynAnchor =>
        _tool is DraftTool.Rect or DraftTool.Circle ? _current[0] : _current[^1];

    void ExitTool(bool commit)
    {
        FinishCurrent(commit);
        _tool = DraftTool.None;
        _phase = LinePhase.Idle;
        SyncToolButtons();
        RefreshPrompt();
        Redraw();
    }

    void SyncToolButtons()
    {
        ToolLine.IsChecked = _tool == DraftTool.Line;
        ToolRect.IsChecked = _tool == DraftTool.Rect;
        ToolCircle.IsChecked = _tool == DraftTool.Circle;
    }

    void FinishCurrent(bool commit)
    {
        if (commit && _current.Count >= 2 && _tool != DraftTool.Circle)
        {
            var pts = _current.ToList();
            if (_mode == PanelDraftMode.Profile && UniqueWorldCount(pts) >= 3 && !NearPt(pts[0], pts[^1]))
                pts.Add(pts[0]);
            var chain = new DraftChain(_mode, pts);
            _current.Clear();
            _hoverSnap = null;
            ClearDyn();
            if (_tool is DraftTool.Line or DraftTool.Rect or DraftTool.Circle)
                _phase = LinePhase.WaitFirst;
            if (_mode == PanelDraftMode.Feature)
            {
                OfferFeature(chain);
                return;
            }
            _chains.Add(chain);
            if (_remnantMode && chain.Mode == PanelDraftMode.Profile && IsClosedWorld(chain.Pts))
            {
                for (var i = _chains.Count - 2; i >= 0; i--)
                {
                    if (_chains[i].Mode == PanelDraftMode.Profile)
                        _chains.RemoveAt(i);
                }
            }
            return;
        }
        _current.Clear();
        _hoverSnap = null;
        ClearDyn();
        if (_tool is DraftTool.Line or DraftTool.Rect or DraftTool.Circle)
            _phase = LinePhase.WaitFirst;
    }

    /// <summary>Leave the active tool. Profile ink stays; unfinished Feature is dropped.</summary>
    void CancelCommand()
    {
        if (_waitDepth)
        {
            _pendingFeature = null;
            _waitDepth = false;
            RefreshPrompt();
            Redraw();
            CommandBox.Focus();
            return;
        }
        ExitTool(commit: _mode != PanelDraftMode.Feature);
    }

    void OfferFeature(DraftChain chain)
    {
        _pendingFeature = chain;
        _waitDepth = true;
        CommandBox.Clear();
        ShowDepthPrompt();
        CommandBox.Focus();
        Redraw();
    }

    void ShowDepthPrompt()
    {
        if (_pendingFeature is not { } pend) return;
        var kind = FeatureKindName(pend);
        var last = _lastFeatureDepth is { } d ? $" · 回车沿用 {FormatDim(d)}" : "";
        CommandPrompt.Text = $"命令: 指定{kind}深度 mm（T=通切，槽可写 深,宽）{last}:";
    }

    static string FeatureKindName(DraftChain c)
    {
        if (c.IsCircle) return "孔";
        if (c.Pts.Count >= 3 && NearPt(c.Pts[0], c.Pts[^1])) return "口袋";
        return "槽";
    }

    void AcceptFeatureDepth(string raw)
    {
        if (_pendingFeature is not { } pend) return;
        double depth;
        double? width = null;
        if (raw.Length == 0)
        {
            if (_lastFeatureDepth is not { } last)
            {
                CommandPrompt.Text = "命令: 必须写入深度 mm（T=通切）";
                return;
            }
            depth = last;
            width = _lastGrooveWidth;
        }
        else if (raw.Equals("T", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("THROUGH", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("通", StringComparison.Ordinal)
            || raw.Equals("通切", StringComparison.Ordinal))
        {
            depth = BoardThickness();
        }
        else if (!TryParseDepth(raw, out depth, out width))
        {
            CommandPrompt.Text = "命令: 无法识别深度（数字，或 T=通切，槽可写 8,6）";
            return;
        }

        if (depth <= 0)
        {
            CommandPrompt.Text = "命令: 深度必须 > 0";
            return;
        }

        _lastFeatureDepth = depth;
        if (width is { } w && w > 0) _lastGrooveWidth = w;
        else if (FeatureKindName(pend) == "槽")
            width = _lastGrooveWidth;

        _chains.Add(pend with { DepthMm = depth, WidthMm = width });
        _pendingFeature = null;
        _waitDepth = false;
        CommandBox.Clear();
        RefreshPrompt();
        Redraw();
    }

    double BoardThickness()
    {
        var thk = SelectedKind()?.ThicknessMm ?? _seed?.ThicknessMm ?? 18;
        return thk > 0.2 ? thk : 18;
    }

    static bool TryParseDepth(string raw, out double depth, out double? width)
    {
        depth = 0;
        width = null;
        var parts = raw.Split([',', ' ', 'x', 'X', '*'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out depth)
            && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.CurrentCulture, out depth))
            return false;
        if (parts.Length >= 2
            && (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                || double.TryParse(parts[1], NumberStyles.Float, CultureInfo.CurrentCulture, out w))
            && w > 0)
            width = w;
        return true;
    }

    void UndoVertex()
    {
        if (_current.Count == 0) return;
        _current.RemoveAt(_current.Count - 1);
        _phase = _current.Count == 0 ? LinePhase.WaitFirst : LinePhase.WaitNext;
        ClearDyn();
        RefreshPrompt();
        Redraw();
    }

    void CloseChain()
    {
        if (UniqueWorldCount(_current) < 3)
        {
            CommandPrompt.Text = "命令: 至少 3 个点才能围成板件外框";
            return;
        }
        if (!NearPt(_current[0], _current[^1]))
            _current.Add(_current[0]);
        FinishCurrent(commit: true);
        if (_waitDepth) return;
        if (_mode == PanelDraftMode.Profile)
        {
            ExitTool(commit: false);
            return;
        }
        RefreshPrompt();
        Redraw();
    }

    void AcceptPoint(WorldPt pt)
    {
        if (_tool == DraftTool.Rect)
        {
            AcceptRectPoint(pt);
            return;
        }
        if (_tool == DraftTool.Circle)
        {
            AcceptCirclePoint(pt);
            return;
        }
        if (_tool != DraftTool.Line) StartLine();
        if (_current.Count >= 3 && NearPt(pt, _current[0]))
        {
            CloseChain();
            return;
        }
        if (_current.Count > 0)
        {
            pt = EffectiveRel(DynAnchor, pt);
            var last = _current[^1];
            if (Math.Abs(last.X - pt.X) < 1e-6 && Math.Abs(last.Y - pt.Y) < 1e-6)
                return;
            if (_current.Count >= 3 && NearPt(pt, _current[0]))
            {
                CloseChain();
                return;
            }
        }
        _current.Add(pt);
        _phase = LinePhase.WaitNext;
        ClearDyn();
        CommandBox.Clear();
        RefreshPrompt();
        Redraw();
    }

    void AcceptRectPoint(WorldPt pt)
    {
        if (_phase != LinePhase.WaitNext || _current.Count == 0)
        {
            _current.Clear();
            _current.Add(pt);
            _phase = LinePhase.WaitNext;
            ClearDyn();
            CommandBox.Clear();
            RefreshPrompt();
            Redraw();
            return;
        }

        var dest = EffectiveRectDest(pt);
        var ring = RectRingFor(_current[0], dest);
        if (ring is null)
        {
            CommandPrompt.Text = "命令: RECTANG 宽高不能为 0";
            return;
        }
        CommitDrawn(new DraftChain(_mode, ring));
    }

    void AcceptCirclePoint(WorldPt pt)
    {
        if (_phase != LinePhase.WaitNext || _current.Count == 0)
        {
            _current.Clear();
            _current.Add(pt);
            _phase = LinePhase.WaitNext;
            ClearDyn();
            CommandBox.Clear();
            RefreshPrompt();
            Redraw();
            return;
        }

        var dest = EffectiveCircleDest(pt);
        var ring = CircleRingFor(_current[0], dest);
        if (ring is null)
        {
            CommandPrompt.Text = _circleDiameter
                ? "命令: CIRCLE 直径不能为 0"
                : "命令: CIRCLE 半径不能为 0";
            return;
        }
        var (c, r) = CircleGeom(_current[0], dest, _circleDiameter);
        CommitDrawn(new DraftChain(_mode, ring, IsCircle: true, Center: c, Radius: r));
    }

    void CommitDrawn(DraftChain chain)
    {
        _current.Clear();
        _phase = LinePhase.WaitFirst;
        ClearDyn();
        CommandBox.Clear();
        if (_mode == PanelDraftMode.Feature)
        {
            OfferFeature(chain);
            return;
        }
        _chains.Add(chain);
        RefreshPrompt();
        Redraw();
    }

    WorldPt LiveRelDest()
    {
        if (_cursor is { } raw)
            return ResolvePoint(raw, updateHover: false).Pt;
        if (_current.Count == 0) return default;
        var a = DynAnchor;
        if (_tool == DraftTool.Rect && _rectFromCenter)
            return new WorldPt(a.X + (_lockDx ?? 0) * 0.5, a.Y + (_lockDy ?? 0) * 0.5);
        if (_tool == DraftTool.Circle)
        {
            var size = _lockDx ?? 0;
            return PointFromHeading(a, size, _lockDy ?? 0);
        }
        return new WorldPt(a.X + (_lockDx ?? 0), a.Y + (_lockDy ?? 0));
    }

    WorldPt EffectiveCircleDest(WorldPt dest)
    {
        if (_current.Count == 0) return dest;
        var a = _current[0];
        var dx = dest.X - a.X;
        var dy = dest.Y - a.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var heading = _lockDy is { } deg
            ? deg
            : dist < 1e-9
                ? 0
                : HeadingFromDelta(dx, dy);
        if (_lockDy is null && _orthoOn)
            heading = Math.Round(heading / 90) * 90;
        var size = _lockDx ?? dist;
        return PointFromHeading(a, size, heading);
    }

    List<WorldPt>? CircleRingFor(WorldPt first, WorldPt dest)
    {
        var dx = dest.X - first.X;
        var dy = dest.Y - first.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.5) return null;
        WorldPt c;
        double r;
        if (_circleDiameter)
        {
            c = new WorldPt(first.X + dx * 0.5, first.Y + dy * 0.5);
            r = dist * 0.5;
        }
        else
        {
            c = first;
            r = dist;
        }
        return CircleRing(c, r);
    }

    static List<WorldPt>? CircleRing(WorldPt c, double r, int segs = 192)
    {
        if (r < 0.25) return null;
        var pts = new List<WorldPt>(segs + 1);
        for (var i = 0; i < segs; i++)
        {
            var a = Math.PI * 2 * i / segs;
            pts.Add(new WorldPt(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r));
        }
        pts.Add(pts[0]);
        return pts;
    }

    static double DegToRad(double deg) => deg * Math.PI / 180;

    static double NormalizeDeg(double deg)
    {
        deg %= 360;
        if (deg < 0) deg += 360;
        return deg;
    }

    /// <summary>Heading from +Y, clockwise. 0 = +Y, 90 = +X.</summary>
    static double HeadingFromDelta(double dx, double dy) =>
        NormalizeDeg(Math.Atan2(dx, dy) * 180 / Math.PI);

    static WorldPt PointFromHeading(WorldPt origin, double size, double headingDeg)
    {
        var a = DegToRad(headingDeg);
        return new WorldPt(origin.X + Math.Sin(a) * size, origin.Y + Math.Cos(a) * size);
    }

    static (WorldPt Center, double Radius) CircleGeom(WorldPt first, WorldPt dest, bool diameter)
    {
        var dx = dest.X - first.X;
        var dy = dest.Y - first.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        return diameter
            ? (new WorldPt(first.X + dx * 0.5, first.Y + dy * 0.5), dist * 0.5)
            : (first, dist);
    }

    WorldPt EffectiveRel(WorldPt anchor, WorldPt dest)
    {
        var sx = dest.X < anchor.X ? -1.0 : 1.0;
        var sy = dest.Y < anchor.Y ? -1.0 : 1.0;
        var dx = _lockDx ?? Math.Abs(dest.X - anchor.X);
        var dy = _lockDy ?? Math.Abs(dest.Y - anchor.Y);
        return new WorldPt(anchor.X + sx * dx, anchor.Y + sy * dy);
    }

    WorldPt EffectiveRectDest(WorldPt dest)
    {
        if (_current.Count == 0) return dest;
        var c = _current[0];
        if (!_rectFromCenter)
            return EffectiveRel(c, dest);
        var sx = dest.X < c.X ? -1.0 : 1.0;
        var sy = dest.Y < c.Y ? -1.0 : 1.0;
        var halfX = _lockDx is { } w ? w * 0.5 : Math.Abs(dest.X - c.X);
        var halfY = _lockDy is { } h ? h * 0.5 : Math.Abs(dest.Y - c.Y);
        return new WorldPt(c.X + sx * halfX, c.Y + sy * halfY);
    }

    List<WorldPt>? RectRingFor(WorldPt first, WorldPt dest)
    {
        if (!_rectFromCenter)
            return RectRing(first, dest);
        var dx = Math.Abs(dest.X - first.X);
        var dy = Math.Abs(dest.Y - first.Y);
        return RectRing(new WorldPt(first.X - dx, first.Y - dy), new WorldPt(first.X + dx, first.Y + dy));
    }

    double DynSpanX(WorldPt dest)
    {
        if (_tool == DraftTool.Circle)
        {
            var dx = dest.X - DynAnchor.X;
            var dy = dest.Y - DynAnchor.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        var d = Math.Abs(dest.X - DynAnchor.X);
        return _tool == DraftTool.Rect && _rectFromCenter ? d * 2 : d;
    }

    double DynSpanY(WorldPt dest)
    {
        if (_tool == DraftTool.Circle)
        {
            var dx = dest.X - DynAnchor.X;
            var dy = dest.Y - DynAnchor.Y;
            return dy == 0 && dx == 0 ? 0 : HeadingFromDelta(dx, dy);
        }
        var d = Math.Abs(dest.Y - DynAnchor.Y);
        return _tool == DraftTool.Rect && _rectFromCenter ? d * 2 : d;
    }

    static string FormatDim(double v)
    {
        var s = v.ToString("0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(s) ? "0" : s;
    }

    static bool TryParseDim(string raw, out double value)
    {
        value = 0;
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return false;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return false;
        value = Math.Abs(v);
        return true;
    }

    bool MoveDynActive =>
        _editorOn && EditDynAnchor is not null;

    bool OrthoHeld => (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

    bool EditorOrtho => _orthoOn || OrthoHeld;

    bool TryHandleDynTab()
    {
        if (MoveDynActive)
            return TryHandleMoveDynTab();
        if (!DynActive) return false;
        var back = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (_dynEdit == DynField.None)
        {
            FocusDyn(back ? DynField.Y : DynField.X);
            return true;
        }

        if (!LockCurrentDyn())
            return true;
        FocusDyn(_dynEdit == DynField.X ? DynField.Y : DynField.X);
        return true;
    }

    bool LockCurrentDyn()
    {
        var raw = (CommandBox.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            var dest = DynDest(LiveRelDest());
            raw = FormatDim(_dynEdit == DynField.X ? DynSpanX(dest) : DynSpanY(dest));
        }
        if (_tool == DraftTool.Circle && _dynEdit == DynField.Y)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
            {
                CommandPrompt.Text = "命令: CIRCLE 无法识别角度";
                return false;
            }
            _lockDy = NormalizeDeg(deg);
            return true;
        }
        if (!TryParseDim(raw, out var v))
        {
            CommandPrompt.Text = _tool switch
            {
                DraftTool.Rect => "命令: RECTANG 宽高不能为 0",
                DraftTool.Circle => "命令: CIRCLE 无法识别尺寸",
                _ => "命令: LINE 无法识别距离",
            };
            return false;
        }
        if (_tool == DraftTool.Rect && v < 0.5)
        {
            CommandPrompt.Text = "命令: RECTANG 宽高不能为 0";
            return false;
        }
        if (_tool == DraftTool.Circle && v < 0.5)
        {
            CommandPrompt.Text = _circleDiameter
                ? "命令: CIRCLE 直径不能为 0"
                : "命令: CIRCLE 半径不能为 0";
            return false;
        }
        if (_dynEdit == DynField.X)
            _lockDx = v;
        else if (_dynEdit == DynField.Y)
            _lockDy = v;
        return true;
    }

    void FocusDyn(DynField field)
    {
        _dynEdit = field;
        _dynTyped = false;
        var dest = DynDest(LiveRelDest());
        var v = field == DynField.X
            ? (_lockDx ?? DynSpanX(dest))
            : (_lockDy ?? DynSpanY(dest));
        _syncingDim = true;
        CommandBox.Text = FormatDim(v);
        _syncingDim = false;
        RefreshPrompt();
        Redraw();
        Dispatcher.BeginInvoke(() =>
        {
            CommandBox.Focus();
            CommandBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void HandleDynEnter()
    {
        if (_dynEdit != DynField.None && !LockCurrentDyn())
            return;
        _dynEdit = DynField.None;
        _dynTyped = false;
        if (_tool == DraftTool.Rect)
            AcceptRectPoint(LiveRelDest());
        else if (_tool == DraftTool.Circle)
            AcceptCirclePoint(LiveRelDest());
        else
            AcceptPoint(LiveRelDest());
    }

    void SyncDynBox()
    {
        if (_dynEdit == DynField.None || _dynTyped || !DynActive)
            return;
        var dest = DynDest(LiveRelDest());
        var v = _dynEdit == DynField.X
            ? (_lockDx ?? DynSpanX(dest))
            : (_lockDy ?? DynSpanY(dest));
        var text = FormatDim(v);
        if (CommandBox.Text == text) return;
        _syncingDim = true;
        CommandBox.Text = text;
        CommandBox.SelectAll();
        _syncingDim = false;
    }

    void OnCommandTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingDim) return;
        if (_dynEdit != DynField.None)
            _dynTyped = true;
    }

    WorldPt DynDest(WorldPt live) => _tool switch
    {
        DraftTool.Rect => EffectiveRectDest(live),
        DraftTool.Circle => EffectiveCircleDest(live),
        _ => EffectiveRel(DynAnchor, live),
    };

    static List<WorldPt>? RectRing(WorldPt a, WorldPt b)
    {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);
        if (maxX - minX < 0.5 || maxY - minY < 0.5)
            return null;
        return
        [
            new(minX, minY),
            new(maxX, minY),
            new(maxX, maxY),
            new(minX, maxY),
            new(minX, minY),
        ];
    }

    void RefreshPrompt()
    {
        if (_editorOn)
        {
            EditRefreshPrompt();
            return;
        }
        SyncCommitChrome();
        if (_waitDepth)
        {
            ShowDepthPrompt();
            return;
        }
        if (_tool == DraftTool.Line)
        {
            if (_phase != LinePhase.WaitNext)
            {
                CommandPrompt.Text = "命令: LINE 指定第一点:";
                return;
            }
            if (_dynEdit == DynField.X)
            {
                CommandPrompt.Text = "命令: LINE 指定相对 X 距离:";
                return;
            }
            if (_dynEdit == DynField.Y)
            {
                CommandPrompt.Text = "命令: LINE 指定相对 Y 距离:";
                return;
            }
            var bits = new List<string>();
            if (_lockDx is { } lx) bits.Add($"ΔX {FormatDim(lx)}");
            if (_lockDy is { } ly) bits.Add($"ΔY {FormatDim(ly)}");
            CommandPrompt.Text = bits.Count == 0
                ? (_mode == PanelDraftMode.Profile
                    ? "命令: LINE 指定下一点 · 回起点或 Enter/C 闭合成板:"
                    : "命令: LINE 指定下一点 或 Tab 输入 ΔX/ΔY 或 Enter 结束:")
                : $"命令: LINE 指定下一点（已锁定 {string.Join(" · ", bits)}）:";
            return;
        }
        if (_tool == DraftTool.Rect)
        {
            if (_phase != LinePhase.WaitNext)
            {
                CommandPrompt.Text = _rectFromCenter
                    ? "命令: RECTANG 指定中心点:"
                    : "命令: RECTANG 指定第一个角点:";
                return;
            }
            if (_dynEdit == DynField.X)
            {
                CommandPrompt.Text = "命令: RECTANG 指定宽度:";
                return;
            }
            if (_dynEdit == DynField.Y)
            {
                CommandPrompt.Text = "命令: RECTANG 指定高度:";
                return;
            }
            var bits = new List<string>();
            if (_lockDx is { } lw) bits.Add($"宽 {FormatDim(lw)}");
            if (_lockDy is { } lh) bits.Add($"高 {FormatDim(lh)}");
            var next = _rectFromCenter ? "指定角点" : "指定对角点";
            CommandPrompt.Text = bits.Count == 0
                ? $"命令: RECTANG {next} 或 Tab 输入宽高:"
                : $"命令: RECTANG {next}（已锁定 {string.Join(" · ", bits)}）:";
            return;
        }
        if (_tool == DraftTool.Circle)
        {
            var sizeName = _circleDiameter ? "直径" : "半径";
            if (_phase != LinePhase.WaitNext)
            {
                CommandPrompt.Text = _circleDiameter
                    ? "命令: CIRCLE 指定边上第一点:"
                    : "命令: CIRCLE 指定圆心:";
                return;
            }
            if (_dynEdit == DynField.X)
            {
                CommandPrompt.Text = $"命令: CIRCLE 指定{sizeName}:";
                return;
            }
            if (_dynEdit == DynField.Y)
            {
                CommandPrompt.Text = "命令: CIRCLE 指定角度（+Y 顺时针）:";
                return;
            }
            var bits = new List<string>();
            if (_lockDx is { } sz) bits.Add($"{sizeName} {FormatDim(sz)}");
            if (_lockDy is { } deg) bits.Add($"角度 {FormatDim(deg)}");
            var next = _circleDiameter ? "指定边上第二点" : "指定边上一点";
            CommandPrompt.Text = bits.Count == 0
                ? $"命令: CIRCLE {next} 或 Tab 输入{sizeName}/角度:"
                : $"命令: CIRCLE {next}（已锁定 {string.Join(" · ", bits)}）:";
            return;
        }

        if (HasBoardOutline())
        {
            CommandPrompt.Text = _remnantMode
                ? "命令: 外框已是余料外形 · 点「加入余料」或再画一块替换"
                : "命令: 外框已是板件定义 · 点「加入方案」或输入 PANEL";
            SyncCommitChrome();
            return;
        }

        CommandPrompt.Text = _remnantMode
            ? "命令: 先画直角外框（REC 矩形 / LINE 多段线，ORTHO 已开）"
            : _mode switch
            {
                PanelDraftMode.Feature => "命令: 指定特征（槽 / 口袋 / 盲孔）· 画完写入深度:",
                PanelDraftMode.Guide => "命令: 指定辅助线:",
                _ => "命令: 先画 Profile 外框（矩形 / 多段线 / 圆）",
            };
        SyncCommitChrome();
    }

    static bool IsAltKey(KeyEventArgs e) =>
        e.Key is Key.LeftAlt or Key.RightAlt
        || (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt);

    void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsAltKey(e)) return;
        if (_cursor is { } raw)
            _hoverSnap = ResolvePoint(raw, updateHover: true).Snap;
        RefreshPrompt();
        Redraw();
        e.Handled = _editorOn && _editPhase == EditPhase.SecondPoint;
    }

    void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (IsAltKey(e))
        {
            if (_cursor is { } raw)
                _hoverSnap = ResolvePoint(raw, updateHover: true).Snap;
            RefreshPrompt();
            Redraw();
            if (_editorOn && _editPhase == EditPhase.SecondPoint)
                e.Handled = true;
            return;
        }
        if (e.Key == Key.F8)
        {
            SetOrtho(!_orthoOn);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F9)
        {
            SetSnap(!_snapOn);
            e.Handled = true;
            return;
        }
        if (_editorOn && EditHandleKey(e))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_tool != DraftTool.None)
            {
                CancelCommand();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Tab)
        {
            if (TryHandleDynTab())
                e.Handled = true;
            return;
        }

        if (ReferenceEquals(e.OriginalSource, CommandBox))
            return;

        if (e.Key == Key.Enter || e.Key == Key.Space || e.Key == Key.Return)
        {
            SubmitCommand();
            e.Handled = true;
        }
    }

    void OnCommandKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            if (TryHandleDynTab())
                e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.Return or Key.Space)
        {
            if (_dynEdit != DynField.None && MoveDynActive)
            {
                HandleMoveDynEnter();
                e.Handled = true;
                return;
            }
            if (_dynEdit != DynField.None && DynActive)
            {
                HandleDynEnter();
                e.Handled = true;
                return;
            }
            SubmitCommand();
            e.Handled = true;
        }
    }

    void SubmitCommand()
    {
        var raw = (CommandBox.Text ?? "").Trim();
        CommandBox.Clear();
        if (_editorOn)
        {
            EditSubmit(raw);
            return;
        }
        if (_waitDepth)
        {
            AcceptFeatureDepth(raw);
            return;
        }
        if (raw.Length == 0)
        {
            if (_tool == DraftTool.Line && _phase == LinePhase.WaitNext)
            {
                if (_mode == PanelDraftMode.Profile && UniqueWorldCount(_current) >= 3)
                    CloseChain();
                else
                    ExitTool(commit: _mode != PanelDraftMode.Feature || UniqueWorldCount(_current) >= 2);
            }
            return;
        }

        if (raw.Equals("PANEL", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("DONE", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("加入", StringComparison.Ordinal)
            || raw.Equals("加入方案", StringComparison.Ordinal)
            || raw.Equals("写回", StringComparison.Ordinal)
            || raw.Equals("写回方案", StringComparison.Ordinal)
            || raw.Equals("加入余料", StringComparison.Ordinal)
            || raw.Equals("写回余料", StringComparison.Ordinal))
        {
            OnCommitClick(this, new RoutedEventArgs());
            return;
        }

        if (raw.Equals("L", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("LINE", StringComparison.OrdinalIgnoreCase))
        {
            StartLine();
            return;
        }
        if (raw.Equals("REC", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("RECT", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("RECTANG", StringComparison.OrdinalIgnoreCase))
        {
            StartRect();
            return;
        }
        if (raw.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("CIR", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("CIRC", StringComparison.OrdinalIgnoreCase))
        {
            StartCircle();
            return;
        }
        if (_tool == DraftTool.Circle
            && (raw.Equals("2P", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("DIA", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("D", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("DIAMETER", StringComparison.OrdinalIgnoreCase)))
        {
            SetCircleDiameter(true);
            return;
        }
        if (_tool == DraftTool.Circle
            && (raw.Equals("CENTER", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("CEN", StringComparison.OrdinalIgnoreCase)))
        {
            SetCircleDiameter(false);
            return;
        }
        if (raw.Equals("U", StringComparison.OrdinalIgnoreCase))
        {
            UndoVertex();
            return;
        }
        if (raw.Equals("CENTER", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("CEN", StringComparison.OrdinalIgnoreCase)
            || (raw.Equals("C", StringComparison.OrdinalIgnoreCase) && _tool == DraftTool.Rect))
        {
            SetRectFromCenter(true);
            return;
        }
        if (raw.Equals("CORNER", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("CO", StringComparison.OrdinalIgnoreCase))
        {
            SetRectFromCenter(false);
            return;
        }
        if (raw.Equals("C", StringComparison.OrdinalIgnoreCase))
        {
            CloseChain();
            return;
        }

        if (TryParsePoint(raw, out var pt))
            AcceptPoint(ResolveTyped(pt));
        else
            CommandPrompt.Text = "命令: 无法识别坐标（x,y 或 @dx,dy）";
    }

    bool TryParsePoint(string raw, out WorldPt pt)
    {
        pt = default;
        var rel = raw.StartsWith('@');
        var body = rel ? raw[1..] : raw;
        var parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;
        if (rel)
        {
            if (_current.Count == 0) return false;
            var last = _current[^1];
            pt = new WorldPt(last.X + x, last.Y + y);
            return true;
        }
        pt = new WorldPt(x, y);
        return true;
    }

    WorldPt ResolveTyped(WorldPt pt)
    {
        if (_tool == DraftTool.Line && _phase == LinePhase.WaitNext && _orthoOn && _current.Count > 0)
            pt = ApplyOrtho(_current[^1], pt);
        return pt;
    }

    void OnDraftRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_editorOn)
        {
            // AutoCAD: right-click = Enter.
            EditSubmitEmpty();
            CommandBox.Focus();
            e.Handled = true;
            return;
        }
        if (_tool != DraftTool.None)
            CancelCommand();
        e.Handled = true;
    }

    void OnDraftDown(object sender, MouseButtonEventArgs e)
    {
        if (_editorOn)
        {
            EditOnLeftDown(e);
            e.Handled = true;
            return;
        }
        if (_waitDepth)
        {
            ShowDepthPrompt();
            CommandBox.Focus();
            e.Handled = true;
            return;
        }
        var screen = ScreenFromMouse(e);
        var raw = ToWorld(screen.X, screen.Y);
        var resolved = ResolvePoint(raw, updateHover: true).Pt;
        if (_tool == DraftTool.None)
            StartLine();
        AcceptPoint(resolved);
        CommandBox.Focus();
        e.Handled = true;
    }

    void OnDraftMove(object sender, MouseEventArgs e)
    {
        var screen = ScreenFromMouse(e);
        if (_panning)
        {
            _ox += screen.X - _panLast.X;
            _oy += screen.Y - _panLast.Y;
            _panLast = screen;
            Redraw();
            return;
        }
        var raw = ToWorld(screen.X, screen.Y);
        _cursor = raw;
        _hoverSnap = ResolvePoint(raw, updateHover: true).Snap;
        if (_editorOn)
        {
            EditOnMove(raw);
            SyncMoveDynBox();
        }
        else
            SyncDynBox();
        Redraw();
    }

    void OnDraftWheel(object sender, MouseWheelEventArgs e)
    {
        var screen = ScreenFromMouse(e);
        var world = ToWorld(screen.X, screen.Y);
        var factor = e.Delta > 0 ? 1.2f : 1f / 1.2f;
        var next = Math.Clamp(_scale * factor, 0.08f, 64f);
        if (Math.Abs(next - _scale) < 1e-6)
        {
            e.Handled = true;
            return;
        }
        _scale = next;
        _ox = screen.X - (float)world.X * _scale;
        _oy = screen.Y + (float)world.Y * _scale;
        e.Handled = true;
        Redraw();
    }

    void OnDraftMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _panLast = ScreenFromMouse(e);
        DraftHost.CaptureMouse();
        DraftHost.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    void OnDraftMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            EndPan();
        else if (e.ChangedButton == MouseButton.Left && _editorOn)
            EditOnLeftUp(e);
    }

    void OnDraftMouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning && e.LeftButton != MouseButtonState.Pressed
            && e.MiddleButton != MouseButtonState.Pressed)
            EndPan();
    }

    void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        if (DraftHost.IsMouseCaptured)
            DraftHost.ReleaseMouseCapture();
        DraftHost.Cursor = Cursors.Arrow;
    }

    (WorldPt Pt, SnapHit? Snap) ResolvePoint(WorldPt raw, bool updateHover)
    {
        var pt = raw;
        WorldPt? orthoAnchor = null;
        if (_tool == DraftTool.Line && _phase == LinePhase.WaitNext && _orthoOn && _current.Count > 0)
        {
            orthoAnchor = _current[^1];
            pt = ApplyOrtho(_current[^1], pt);
        }
        if (_editorOn && EditorOrtho && EditDynAnchor is { } editAnchor)
        {
            orthoAnchor = editAnchor;
            pt = ApplyOrtho(editAnchor, pt);
        }

        var osnap = FindOsnap(raw);
        if (osnap is { } hit)
        {
            var snapped = hit.Pt;
            if (orthoAnchor is { } a)
                snapped = ApplyOrtho(a, snapped);
            return (snapped, hit);
        }

        if (_snapOn)
            pt = GridSnap(pt);
        _ = updateHover;
        return (pt, null);
    }

    static WorldPt ApplyOrtho(WorldPt last, WorldPt raw)
    {
        var dx = raw.X - last.X;
        var dy = raw.Y - last.Y;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? new WorldPt(raw.X, last.Y)
            : new WorldPt(last.X, raw.Y);
    }

    static WorldPt GridSnap(WorldPt pt) =>
        new(Math.Round(pt.X / MinorMm) * MinorMm, Math.Round(pt.Y / MinorMm) * MinorMm);

    SnapHit? FindOsnap(WorldPt raw)
    {
        var (sx, sy) = ToScreen(raw.X, raw.Y);
        SnapHit? best = null;
        var bestD = OsnapPx;
        foreach (var c in Candidates())
        {
            var (cx, cy) = ToScreen(c.Pt.X, c.Pt.Y);
            var d = MathF.Sqrt((cx - sx) * (cx - sx) + (cy - sy) * (cy - sy));
            if (d > bestD) continue;
            if (best is { } cur && Math.Abs(d - bestD) < 0.4f && Rank(c.Kind) >= Rank(cur.Kind))
                continue;
            bestD = d;
            best = c;
        }
        return best;
    }

    static int Rank(SnapKind k) => k switch
    {
        SnapKind.Close => 0,
        SnapKind.End => 1,
        SnapKind.Mid => 2,
        _ => 3,
    };

    IEnumerable<SnapHit> Candidates()
    {
        if (_tool == DraftTool.Line && _current.Count >= 3)
            yield return new SnapHit(_current[0], SnapKind.Close);
        yield return new SnapHit(new WorldPt(0, 0), SnapKind.Origin);
        if (_editorOn)
        {
            foreach (var c in EditSnapCandidates())
                yield return c;
            yield break;
        }
        foreach (var chain in EnumerateChains())
        {
            for (var i = 0; i < chain.Count; i++)
            {
                yield return new SnapHit(chain[i], SnapKind.End);
                if (i == 0) continue;
                var a = chain[i - 1];
                var b = chain[i];
                yield return new SnapHit(new WorldPt((a.X + b.X) / 2, (a.Y + b.Y) / 2), SnapKind.Mid);
            }
        }
    }

    IEnumerable<IReadOnlyList<WorldPt>> EnumerateChains()
    {
        foreach (var c in _chains) yield return c.Pts;
        if (_current.Count > 0) yield return _current;
    }

    (float X, float Y) ScreenFromMouse(MouseEventArgs e)
    {
        RefreshDpi();
        var pos = e.GetPosition(DraftHost);
        return ((float)(pos.X * _dpiX), (float)(pos.Y * _dpiY));
    }

    void RefreshDpi()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return;
        var m = src.CompositionTarget.TransformToDevice;
        _dpiX = m.M11;
        _dpiY = m.M22;
    }

    WorldPt ToWorld(float sx, float sy) =>
        new((sx - _view.Ox) / _view.Scale, (_view.Oy - sy) / _view.Scale);

    (float X, float Y) ToScreen(double wx, double wy) =>
        (_view.Ox + (float)wx * _view.Scale, _view.Oy - (float)wy * _view.Scale);

    void Redraw() => DraftHost.InvalidateVisual();

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        if (!_viewReady)
        {
            if (_editorOn)
            {
                FitEditorView(w, h);
            }
            else
            {
                _ox = OriginInset * (float)_dpiX;
                _oy = h - OriginInset * (float)_dpiY;
            }
            _viewReady = true;
        }
        _view = new DraftView(_ox, _oy, _scale, w, h);
        canvas.Clear(SKColors.Black);

        DrawGrid(canvas, _view);
        DrawAxes(canvas, _view);
        DrawUcsIcon(canvas, 18, h - 18);
        if (_editorOn)
        {
            DrawEditor(canvas);
        }
        else
        {
            DrawChains(canvas);
            DrawRubber(canvas);
        }
        DrawSnapMarker(canvas);
    }

    static SKColor LayerColor(PanelDraftMode mode) => mode switch
    {
        PanelDraftMode.Feature => new SKColor(0xE2, 0x4A, 0x4A),
        PanelDraftMode.Guide => new SKColor(0x3C, 0xBF, 0x5A),
        _ => new SKColor(0x4A, 0x9A, 0xE8),
    };

    void DrawChains(SKCanvas canvas)
    {
        var closedProfiles = _chains.Where(c => c.Mode == PanelDraftMode.Profile && IsClosedWorld(c.Pts)).ToList();
        if (closedProfiles.Count > 0)
        {
            using var fill = new SKPaint
            {
                Color = new SKColor(0x4A, 0x9A, 0xE8, 0x32),
                IsAntialias = true,
            };
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var chain in closedProfiles)
                AppendClosed(path, chain.Pts);
            canvas.DrawPath(path, fill);
        }
        foreach (var chain in _chains)
        {
            using var ink = LayerPaint(chain.Mode, dashed: chain.Mode == PanelDraftMode.Guide);
            DrawChain(canvas, chain.Pts, ink);
            DrawFeatureDepth(canvas, chain);
        }
        if (_pendingFeature is { } pend)
        {
            using var ink = LayerPaint(pend.Mode, dashed: false);
            DrawChain(canvas, pend.Pts, ink);
        }
        if (_current.Count >= 2)
        {
            using var ink = LayerPaint(_mode, dashed: _mode == PanelDraftMode.Guide);
            DrawChain(canvas, _current, ink);
        }
    }

    void AppendClosed(SKPath path, IReadOnlyList<WorldPt> chain)
    {
        if (chain.Count < 3) return;
        var a0 = ToScreen(chain[0].X, chain[0].Y);
        path.MoveTo((float)a0.X, (float)a0.Y);
        for (var i = 1; i < chain.Count; i++)
        {
            var a = ToScreen(chain[i].X, chain[i].Y);
            path.LineTo((float)a.X, (float)a.Y);
        }
        path.Close();
    }

    void DrawFeatureDepth(SKCanvas canvas, DraftChain chain)
    {
        if (chain.Mode != PanelDraftMode.Feature || chain.DepthMm is not { } d || d <= 0)
            return;
        if (chain.Pts.Count == 0) return;
        var mid = chain.IsCircle
            ? chain.Center
            : new WorldPt(chain.Pts.Average(p => p.X), chain.Pts.Average(p => p.Y));
        var (sx, sy) = ToScreen(mid.X, mid.Y);
        var label = chain.WidthMm is { } w && w > 0
            ? $"d{FormatDim(d)} w{FormatDim(w)}"
            : $"d{FormatDim(d)}";
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 11);
        using var ink = new SKPaint { Color = new SKColor(0xE2, 0x4A, 0x4A), IsAntialias = true };
        canvas.DrawText(label, sx + 6, sy - 6, SKTextAlign.Left, font, ink);
    }

    static SKPaint LayerPaint(PanelDraftMode mode, bool dashed) =>
        new()
        {
            Color = LayerColor(mode),
            StrokeWidth = 1.15f,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            PathEffect = dashed ? SKPathEffect.CreateDash([7, 4], 0) : null,
        };

    void DrawChain(SKCanvas canvas, IReadOnlyList<WorldPt> chain, SKPaint paint)
    {
        if (chain.Count < 2) return;
        using var path = new SKPath();
        var a0 = ToScreen(chain[0].X, chain[0].Y);
        path.MoveTo(a0.X, a0.Y);
        for (var i = 1; i < chain.Count; i++)
        {
            var a = ToScreen(chain[i].X, chain[i].Y);
            path.LineTo(a.X, a.Y);
        }
        canvas.DrawPath(path, paint);
    }

    void DrawRubber(SKCanvas canvas)
    {
        if (_phase != LinePhase.WaitNext || _current.Count == 0 || _cursor is not { } raw)
            return;
        var dest = ResolvePoint(raw, updateHover: false).Pt;
        using var dash = new SKPaint
        {
            Color = LayerColor(_mode),
            StrokeWidth = 1.15f,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
        };

        if (_tool == DraftTool.Circle)
        {
            dest = EffectiveCircleDest(dest);
            var (c, r) = CircleGeom(_current[0], dest, _circleDiameter);
            if (r >= 0.25)
            {
                var (sx, sy) = ToScreen(c.X, c.Y);
                canvas.DrawCircle(sx, sy, (float)(r * _view.Scale), dash);
            }
            var spokeA = ToScreen(_current[0].X, _current[0].Y);
            var spokeB = ToScreen(dest.X, dest.Y);
            canvas.DrawLine(spokeA.X, spokeA.Y, spokeB.X, spokeB.Y, dash);
            if (!_circleDiameter)
                DrawCenterMark(canvas, _current[0]);
            else
                DrawCenterMark(canvas, c);
            DrawCircleDynDims(canvas, _current[0], dest, c);
            return;
        }

        if (_tool == DraftTool.Rect)
        {
            dest = EffectiveRectDest(dest);
            var ring = RectRingFor(_current[0], dest) ??
            [
                _current[0],
                new(dest.X, _current[0].Y),
                dest,
                new(_current[0].X, dest.Y),
                _current[0],
            ];
            for (var i = 1; i < ring.Count; i++)
            {
                var a = ToScreen(ring[i - 1].X, ring[i - 1].Y);
                var b = ToScreen(ring[i].X, ring[i].Y);
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, dash);
            }
            if (_rectFromCenter)
                DrawCenterMark(canvas, _current[0]);
            var dimA = ring[0];
            var dimB = ring.Count >= 3 ? ring[2] : dest;
            DrawRectDynDims(canvas, dimA, dimB);
            return;
        }

        dest = EffectiveRel(_current[^1], dest);
        var from = ToScreen(_current[^1].X, _current[^1].Y);
        var to = ToScreen(dest.X, dest.Y);
        canvas.DrawLine(from.X, from.Y, to.X, to.Y, dash);
        DrawLineDynDims(canvas, _current[^1], dest);
    }

    void DrawLineDynDims(SKCanvas canvas, WorldPt last, WorldPt dest)
    {
        var dx = dest.X - last.X;
        var dy = dest.Y - last.Y;
        var gap = 18f / Math.Max(_view.Scale, 0.01f);
        var yOff = dy >= 0 ? -gap : gap;
        var xOff = dx >= 0 ? gap : -gap;
        DrawCadDim(canvas, new WorldPt(last.X, last.Y + yOff), new WorldPt(dest.X, last.Y + yOff),
            FormatDim(Math.Abs(dx)), horizontal: true,
            _dynEdit == DynField.X, _lockDx is not null);
        DrawCadDim(canvas, new WorldPt(dest.X + xOff, last.Y), new WorldPt(dest.X + xOff, dest.Y),
            FormatDim(Math.Abs(dy)), horizontal: false,
            _dynEdit == DynField.Y, _lockDy is not null);
    }

    void DrawCircleDynDims(SKCanvas canvas, WorldPt first, WorldPt dest, WorldPt center)
    {
        var size = DynSpanX(dest);
        var deg = DynSpanY(dest);
        var gap = 16f / Math.Max(_view.Scale, 0.01f);
        var dx = dest.X - first.X;
        var dy = dest.Y - first.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 1e-6)
        {
            var px = -dy / len * gap;
            var py = dx / len * gap;
            DrawCadDim(canvas,
                new WorldPt(first.X + px, first.Y + py),
                new WorldPt(dest.X + px, dest.Y + py),
                FormatDim(size),
                horizontal: Math.Abs(dx) >= Math.Abs(dy),
                _dynEdit == DynField.X, _lockDx is not null);
        }

        var (ox, oy) = ToScreen(center.X, center.Y);
        var arcR = 34f;
        using var pen = new SKPaint
        {
            Color = _dynEdit == DynField.Y || _lockDy is not null
                ? new SKColor(0xE8, 0xC8, 0x4A)
                : new SKColor(0x7A, 0xC8, 0xE8),
            StrokeWidth = 1.1f,
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawLine(ox, oy, ox, oy - arcR, pen);
        if (Math.Abs(deg) > 0.05)
        {
            using var path = new SKPath();
            path.AddArc(SKRect.Create(ox - arcR, oy - arcR, arcR * 2, arcR * 2), -90, (float)deg);
            canvas.DrawPath(path, pen);
        }

        var mid = PointFromHeading(center, arcR / Math.Max(_view.Scale, 0.01f), deg * 0.5);
        var label = $"{FormatDim(deg)}°";
        var lx = mid.X;
        var ly = mid.Y;
        DrawCadDim(canvas, new WorldPt(lx, ly), new WorldPt(lx, ly),
            label, horizontal: true,
            _dynEdit == DynField.Y, _lockDy is not null);
    }

    void DrawCenterMark(SKCanvas canvas, WorldPt c)
    {
        var (sx, sy) = ToScreen(c.X, c.Y);
        using var pen = new SKPaint
        {
            Color = new SKColor(0xE8, 0xC8, 0x4A),
            StrokeWidth = 1.2f,
            IsStroke = true,
            IsAntialias = true,
        };
        const float r = 7;
        canvas.DrawLine(sx - r, sy, sx + r, sy, pen);
        canvas.DrawLine(sx, sy - r, sx, sy + r, pen);
        canvas.DrawCircle(sx, sy, 2.2f, pen);
    }

    void DrawRectDynDims(SKCanvas canvas, WorldPt first, WorldPt dest)
    {
        var minX = Math.Min(first.X, dest.X);
        var maxX = Math.Max(first.X, dest.X);
        var minY = Math.Min(first.Y, dest.Y);
        var maxY = Math.Max(first.Y, dest.Y);
        var w = maxX - minX;
        var h = maxY - minY;
        var gap = 18f / Math.Max(_view.Scale, 0.01f);

        DrawCadDim(canvas, new WorldPt(minX, minY - gap), new WorldPt(maxX, minY - gap),
            FormatDim(w), horizontal: true,
            _dynEdit == DynField.X, _lockDx is not null);
        DrawCadDim(canvas, new WorldPt(minX - gap, minY), new WorldPt(minX - gap, maxY),
            FormatDim(h), horizontal: false,
            _dynEdit == DynField.Y, _lockDy is not null);
    }

    void DrawCadDim(SKCanvas canvas, WorldPt a, WorldPt b, string text, bool horizontal,
        bool editing, bool locked)
    {
        var sa = ToScreen(a.X, a.Y);
        var sb = ToScreen(b.X, b.Y);
        var tick = 5f;
        using var line = new SKPaint
        {
            Color = locked || editing ? new SKColor(0xE8, 0xC8, 0x4A) : new SKColor(0x7A, 0xC8, 0xE8),
            StrokeWidth = 1.1f,
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawLine(sa.X, sa.Y, sb.X, sb.Y, line);
        if (horizontal)
        {
            canvas.DrawLine(sa.X, sa.Y - tick, sa.X, sa.Y + tick, line);
            canvas.DrawLine(sb.X, sb.Y - tick, sb.X, sb.Y + tick, line);
        }
        else
        {
            canvas.DrawLine(sa.X - tick, sa.Y, sa.X + tick, sa.Y, line);
            canvas.DrawLine(sb.X - tick, sb.Y, sb.X + tick, sb.Y, line);
        }

        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 11);
        var tw = font.MeasureText(text);
        var boxW = tw + (locked ? 22 : 12);
        var boxH = 16f;
        var cx = (sa.X + sb.X) / 2;
        var cy = (sa.Y + sb.Y) / 2;
        var left = cx - boxW / 2;
        var top = cy - boxH / 2;
        using var fill = new SKPaint
        {
            Color = editing ? new SKColor(0x2A, 0x3A, 0x12) : new SKColor(0x0D, 0x21, 0x37),
            IsAntialias = true,
        };
        using var border = new SKPaint
        {
            Color = editing ? new SKColor(0xE8, 0xC8, 0x4A) : locked
                ? new SKColor(0xE8, 0xC8, 0x4A)
                : new SKColor(0x3D, 0x7A, 0xB5),
            StrokeWidth = editing ? 1.6f : 1.2f,
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawRect(left, top, boxW, boxH, fill);
        canvas.DrawRect(left, top, boxW, boxH, border);
        using var ink = new SKPaint
        {
            Color = locked || editing ? new SKColor(0xE8, 0xC8, 0x4A) : new SKColor(0xE8, 0xE8, 0xE8),
            IsAntialias = true,
        };
        var textX = left + (locked ? 16 : 6);
        canvas.DrawText(text, textX, top + 12, SKTextAlign.Left, font, ink);
        if (locked)
            DrawLockGlyph(canvas, left + 4, top + 3, ink);
    }

    static void DrawLockGlyph(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        using var stroke = new SKPaint
        {
            Color = paint.Color,
            StrokeWidth = 1.2f,
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawRect(x, y + 4, 8, 7, stroke);
        canvas.DrawArc(new SKRect(x + 1.2f, y, x + 6.8f, y + 6), 180, 180, false, stroke);
    }

    void DrawSnapMarker(SKCanvas canvas)
    {
        if (_hoverSnap is not { } hit) return;
        var (sx, sy) = ToScreen(hit.Pt.X, hit.Pt.Y);
        using var pen = new SKPaint
        {
            Color = new SKColor(0x3D, 0xE2, 0xE2),
            StrokeWidth = 1.4f,
            IsStroke = true,
            IsAntialias = true,
        };
        const float r = 6;
        switch (hit.Kind)
        {
            case SnapKind.Close:
                pen.Color = new SKColor(0xE8, 0xC8, 0x4A);
                canvas.DrawRect(sx - r, sy - r, r * 2, r * 2, pen);
                canvas.DrawCircle(sx, sy, 2.2f, pen);
                break;
            case SnapKind.End:
                canvas.DrawRect(sx - r, sy - r, r * 2, r * 2, pen);
                break;
            case SnapKind.Mid:
                using (var path = new SKPath())
                {
                    path.MoveTo(sx, sy - r);
                    path.LineTo(sx + r, sy + r);
                    path.LineTo(sx - r, sy + r);
                    path.Close();
                    canvas.DrawPath(path, pen);
                }
                break;
            default:
                canvas.DrawCircle(sx, sy, r, pen);
                break;
        }
    }

    static void DrawGrid(SKCanvas canvas, DraftView view)
    {
        var minX = (0 - view.Ox) / view.Scale;
        var maxX = (view.W - view.Ox) / view.Scale;
        var minY = (view.Oy - view.H) / view.Scale;
        var maxY = view.Oy / view.Scale;
        var minorMm = NiceGridStep(14f / Math.Max(view.Scale, 0.01f));
        var majorMm = minorMm * 5;
        if ((maxX - minX) / minorMm > 400)
            minorMm = NiceGridStep((maxX - minX) / 200);

        using var minor = new SKPaint
        {
            Color = new SKColor(0x22, 0x22, 0x22),
            StrokeWidth = 1f,
            IsStroke = true,
            IsAntialias = true,
        };
        using var major = new SKPaint
        {
            Color = new SKColor(0x38, 0x38, 0x38),
            StrokeWidth = 1f,
            IsStroke = true,
            IsAntialias = true,
        };

        var x0 = MathF.Floor(minX / minorMm) * minorMm;
        for (var x = x0; x <= maxX + minorMm; x += minorMm)
        {
            var sx = view.Ox + x * view.Scale;
            var isMajor = Math.Abs(x % majorMm) < minorMm * 0.01f;
            canvas.DrawLine(sx, 0, sx, view.H, isMajor ? major : minor);
        }
        var y0 = MathF.Floor(minY / minorMm) * minorMm;
        for (var y = y0; y <= maxY + minorMm; y += minorMm)
        {
            var sy = view.Oy - y * view.Scale;
            var isMajor = Math.Abs(y % majorMm) < minorMm * 0.01f;
            canvas.DrawLine(0, sy, view.W, sy, isMajor ? major : minor);
        }
    }

    static float NiceGridStep(float worldMm)
    {
        if (worldMm < 0.5f) worldMm = 0.5f;
        var mag = MathF.Pow(10, MathF.Floor(MathF.Log10(worldMm)));
        var n = worldMm / mag;
        var nice = n < 1.5f ? 1f : n < 3.5f ? 2f : n < 7.5f ? 5f : 10f;
        return nice * mag;
    }

    static void DrawAxes(SKCanvas canvas, DraftView view)
    {
        using var xAxis = new SKPaint
        {
            Color = new SKColor(0xC8, 0x28, 0x28),
            StrokeWidth = 1.2f,
            IsStroke = true,
            IsAntialias = true,
        };
        using var yAxis = new SKPaint
        {
            Color = new SKColor(0x28, 0xB0, 0x28),
            StrokeWidth = 1.2f,
            IsStroke = true,
            IsAntialias = true,
        };
        canvas.DrawLine(0, view.Oy, view.W, view.Oy, xAxis);
        canvas.DrawLine(view.Ox, 0, view.Ox, view.H, yAxis);

        using var xLabel = new SKPaint { Color = new SKColor(0xC8, 0x28, 0x28), IsAntialias = true };
        using var yLabel = new SKPaint { Color = new SKColor(0x28, 0xB0, 0x28), IsAntialias = true };
        using var origin = new SKPaint { Color = new SKColor(0x9A, 0x9A, 0x9A), IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 11);
        canvas.DrawText("0,0", view.Ox + 6, view.Oy - 6, SKTextAlign.Left, font, origin);
        canvas.DrawText("X", view.W - 18, view.Oy - 6, SKTextAlign.Left, font, xLabel);
        canvas.DrawText("Y", view.Ox + 6, 16, SKTextAlign.Left, font, yLabel);
    }

    static void DrawUcsIcon(SKCanvas canvas, float x, float y)
    {
        using var xPen = new SKPaint
        {
            Color = new SKColor(0xC8, 0x28, 0x28),
            StrokeWidth = 2,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        using var yPen = new SKPaint
        {
            Color = new SKColor(0x28, 0xB0, 0x28),
            StrokeWidth = 2,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
        };
        using var text = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xC8), IsAntialias = true };
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 10);
        canvas.DrawLine(x, y, x + 28, y, xPen);
        canvas.DrawLine(x, y, x, y - 28, yPen);
        canvas.DrawText("x", x + 30, y + 3, SKTextAlign.Left, font, text);
        canvas.DrawText("y", x - 3, y - 32, SKTextAlign.Left, font, text);
    }
}
