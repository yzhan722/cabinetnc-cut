using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CabinetNC.Application.Projects;
using CabinetNC.Compute.Contracts;
using CabinetNC.Desktop.Worker;
using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Machines;
using CabinetNC.Domain.Manufacturing;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using CabinetNC.FusionPackage;
using CabinetNC.Infrastructure.Diagnostics;
using CabinetNC.Infrastructure.Library;
using CabinetNC.Infrastructure.Projects;
using Microsoft.Win32;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using PanelPart = CabinetNC.Domain.Parts.Panel;

namespace CabinetNC.Desktop;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty StockKindPickerVisibleProperty =
        DependencyProperty.Register(
            nameof(StockKindPickerVisible),
            typeof(Visibility),
            typeof(MainWindow),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility StockKindPickerVisible
    {
        get => (Visibility)GetValue(StockKindPickerVisibleProperty);
        set => SetValue(StockKindPickerVisibleProperty, value);
    }

    readonly ProjectSession _session = new();
    readonly WorkerProcessHost _worker = new();
    readonly SqliteProjectStore _store = new();
    WorkshopLibrary _library = WorkshopLibraryStore.Load();
    readonly HashSet<string> _locked = new(StringComparer.Ordinal);
    PanelPart? _selected;
    PanelPart? _clipboardPanel;
    readonly List<PanelPart> _clipboardNest = [];
    StartNestingReply? _nest;
    IReadOnlyList<NestSheetSpec> _nestSheetsUsed = [];
    IReadOnlyList<PartInPartSlot> _partInPartSlots = [];
    readonly Dictionary<int, GuillotineCutPlanner.SheetPlan> _guillotineBySheet = new();
    readonly List<HeldNestPart> _nestHolding = [];
    List<CanvasPainter.NestHoldingItem> _holdingLayout = [];
    List<CanvasPainter.NestHoldingRegion> _holdingRegions = [];
    float _holdingBayLeft;
    bool _nestDragFromHold;
    bool _holdPreviewOnSheet;
    bool _holdPreviewBlocked;
    readonly List<CanvasPainter.HoldPreviewPart> _holdPreviewPlaces = [];
    int _activeNestSheet;
    bool _showNest;
    string _stage = "load";
    const double LeftRailMinW = 140;
    const double LeftRailMaxW = 560;
    const double LeftRailDefaultW = 200;
    double _leftRailWidth = LeftRailDefaultW;
    readonly HashSet<NestGroupKey> _pickedStockKinds = [];
    bool _syncingProjectName;
    string _module = "production";
    bool _nestBusy;
    bool _stageChanging;
    bool _enableTongue = true;
    bool _enableProfile = true;
    bool _enableProfileLast = true;
    bool _enableClearance = true;
    bool _enableBridges = true;
    bool _enableDrilling = true;
    TroyPassKind? _opsFocus;
    CamStrategyKind? _opsStrategy;
    string _opsSummary = "";
    string _profFirstTool = "T2";
    string _profLastTool = "T2";
    double _profFirstFeed = 12000;
    double _profFirstRpm = 14500;
    double _profFirstPlunge = 1000;
    bool _profFirstRamp45;
    double _profFirstLeave = 0.5;
    double _profLastFeed = 20000;
    double _profLastRpm = 14500;
    double _profLastPlunge = 1000;
    double _profLastThrough = -0.55;
    double _tongueFeed = TroyRecipe.TongueFeedMmMin;
    double _tongueRpm = TroyRecipe.SpindleRpm;
    double _tonguePlunge = TroyRecipe.PlungeFeedMmMin;
    bool _homeXyAtEnd = true;
    double _profBridgeWidth = ProfileBridgePlanner.DefaultWidthMm;
    double _profTinyAreaM2 = ProfileBridgePlanner.TinyAreaM2;
    double _profLargeAreaM2 = ProfileBridgePlanner.LargeAreaM2;
    double _profStripAspect = ProfileBridgePlanner.StripAspect;
    double _clrFeed = TroyRecipe.WorkFirstFeedMmMin;
    double _clrRpm = TroyRecipe.SpindleRpm;
    double _clrPlunge = TroyRecipe.PlungeFeedMmMin;
    double _clrLargeMinShort = ClearanceToolPick.LargeMinShortMm;
    double _drillPlunge = TroyRecipe.PlungeFeedMmMin;
    double _drillRpm = TroyRecipe.SpindleRpm;
    double _drillThrough = TroyRecipe.ThroughZMm;
    double _drillMaxExclusive = ClearanceToolPick.DrillMaxExclusiveMm;
    double _guillotineFeed = TroyRecipe.GuillotineFeedMmMin;
    double _guillotinePlunge = TroyRecipe.GuillotinePlungeMmMin;
    double _guillotineThrough = TroyRecipe.GuillotineThroughZMm;
    bool _bridgeManualMode;
    bool _bridgeDeleteMode;
    readonly List<ProfileBridge> _profileBridges = [];
    bool _opsAllSheets = true;
    bool _syncingOpsStrategy;
    bool _syncingOpsIcons;
    string? _activeToolId;
    IReadOnlyList<CamFrame> _camFrames = [];
    int _camFrameIndex;
    readonly DispatcherTimer _camTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    IReadOnlyList<ToolStroke> _ncSimStrokes = [];
    double _ncSimTime;
    double _ncSimTotal;
    double _ncSimSpeed = 4;
    bool _ncSimPlaying;
    bool _syncingNcSimSlider;
    readonly DispatcherTimer _ncSimTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    float _simUserScale;
    float _simOx, _simOy;
    bool _simPanning;
    float _simPanStartX, _simPanStartY, _simPanOrigX, _simPanOrigY;
    float _nestOriginX, _nestOriginY;

    // drag state
    string? _dragMode; // geom | nest | nestBox | label
    GeomInteraction.Hit? _geomHit;
    PanelPart? _geomStart;
    string? _nestDragPanelId;
    readonly Dictionary<string, (double X, double Y)> _labelOverrides = new(StringComparer.Ordinal);
    double _nestStartMx, _nestStartMy, _nestOrigOx, _nestOrigOy;
    double _nestDragRotDeg;
    readonly HashSet<string> _nestSelected = new(StringComparer.Ordinal);
    string? _nestContextPanelId;
    bool _nestContextInHold;
    readonly HashSet<string> _retargetFocusIds = new(StringComparer.Ordinal);
    readonly Dictionary<string, (double Ox, double Oy, double Rot)> _nestGroupOrig = new(StringComparer.Ordinal);
    double _holdSlideOx, _holdSlideOy;
    bool _holdSlideHasValid;
    double _nestBoxX0, _nestBoxY0, _nestBoxX1, _nestBoxY1;
    bool _syncingNestSelection;
    GeomInteraction.View? _geomView;
    float _nestPad, _nestScale, _nestSheetH, _nestSheetW;
    int _surfaceW, _surfaceH;
    double _dpiX = 1, _dpiY = 1;
    string? _hoverHint;
    IReadOnlyList<CutOp> _opsOverlay = [];
    List<ExportNcFile> _exportFiles = [];
    ExportNcFile? _exportSelected;
    bool _syncingExportFiles;
    readonly List<StockMaterialKindVm> _stockKinds = [];
    bool _syncingMachineCombo;

    public MainWindow()
    {
        InitializeComponent();
        // The executing-block marker is an overlay; keep it glued to its line while the
        // operator scrolls or the pane is resized.
        NcPreview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => PositionNcHighlight()));
        NcPreview.SizeChanged += (_, _) => PositionNcHighlight();
        foreach (var m in MachineCatalog.All)
        {
            MachineCombo.Items.Add(m);
            StockMachineCombo.Items.Add(m);
            StockLabelerCombo.Items.Add(m);
            MachineComboModule.Items.Add(m);
            OpsMachineCombo.Items.Add(m);
        }
        MachineCombo.SelectedValue = MachineCatalog.DefaultId;
        StockMachineCombo.SelectedValue = MachineCatalog.DefaultId;
        StockLabelerCombo.SelectedValue = MachineCatalog.DefaultId;
        MachineComboModule.SelectedValue = MachineCatalog.DefaultId;
        OpsMachineCombo.SelectedValue = MachineCatalog.DefaultId;
        BindOpsToolCombos();
        ApplyLibraryToSettingsUi();
        ApplyLibraryToNestBoxes();
        StageTabs.SelectedIndex = 0;
        HighlightModule();
        ApplyModuleVisibility();
        ApplyStageVisibility();
        UpdateCanvasHint();
        UpdateStageChrome();
        RefreshWorkflowDots();
        RefreshEmptyState();
        _camTimer.Tick += (_, _) => StepCam(1);
        _ncSimTimer.Tick += (_, _) => TickNcSim();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => CanvasHost.InvalidateVisual();
        PreviewMouseRightButtonDown += OnWindowRightDown;
        PreviewMouseRightButtonUp += OnWindowRightUp;
        SourceInitialized += (_, _) =>
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
        };

        Loaded += async (_, _) =>
        {
            UsageLog.LogEvent("ui", "desktop.mainLoaded", new Dictionary<string, object?>
            {
                ["logDirs"] = UsageLog.LogDirs().ToList(),
                ["machineId"] = SelectedMachineId(),
            });
            UpdateStageChrome();
            RefreshWorkflowDots();
            RefreshEmptyState();
            RefreshRecentUi();
            SyncProjectNameBox();
            SetStatus("就绪 · 打开方案或示例开始（Ctrl+O）");
            // Worker probing can take seconds; never let it overwrite a status the operator
            // has since produced by working.
            await RefreshWorkerAsync();
        };
        Closing += OnWindowClosing;
        Closed += async (_, _) =>
        {
            UsageLog.LogEvent("ui", "desktop.mainClosed");
            _camTimer.Stop();
            StopNcSim();
            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
            await _worker.DisposeAsync();
        };
    }

    string SelectedMachineId() =>
        MachineCombo.SelectedValue as string
        ?? StockMachineCombo.SelectedValue as string
        ?? (MachineCombo.SelectedItem as MachineProfile)?.Id
        ?? (StockMachineCombo.SelectedItem as MachineProfile)?.Id
        ?? MachineCatalog.DefaultId;

    string SelectedLabelerMachineId() =>
        StockLabelerCombo.SelectedValue as string
        ?? (StockLabelerCombo.SelectedItem as MachineProfile)?.Id
        ?? MachineCatalog.DefaultId;

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // CAD convention: Enter applies a numeric field (the LostFocus handlers commit),
        // Esc leaves it. The stock-kind rename box has its own Enter/Esc semantics.
        if (Keyboard.FocusedElement is TextBox { AcceptsReturn: false } field
            && field.Tag as string != "KindRename")
        {
            if (e.Key == Key.Enter)
            {
                field.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                Keyboard.ClearFocus();
                Focus();
                e.Handled = true;
                return;
            }
        }

        if (!IsTypingTarget() && Keyboard.Modifiers == ModifierKeys.None && ViewportActive())
        {
            if (e.Key == Key.Space && _stage == "out" && _ncSimStrokes.Count > 0)
            {
                OnOutSimPlayClick(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.F or Key.Home)
            {
                FitViewport();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemPlus or Key.Add)
            {
                ZoomViewportCentered(1.25);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                ZoomViewportCentered(1 / 1.25);
                e.Handled = true;
                return;
            }
        }

        if (IsAltKey(e) && _dragMode == "nest")
        {
            if (!e.IsRepeat)
                ApplyNestDragAtLastPointer();
            e.Handled = true;
            return;
        }

        if (IsSKey(e) && _dragMode == "nest" && !IsTypingTarget())
        {
            if (!e.IsRepeat)
                ApplyNestDragAtLastPointer();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.O)
        {
            OnOpenProjectClick(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    OnOpenClick(sender, e);
                    e.Handled = true;
                    return;
                case Key.S:
                    OnSaveProjectClick(sender, e);
                    e.Handled = true;
                    return;
                case Key.E:
                    if (OneClickExportBtn.IsEnabled)
                        OnOneClickExportClick(sender, e);
                    else
                        SetStatus("一键导出需要先完成密排和刀路", StatusKind.Warning);
                    e.Handled = true;
                    return;
                case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 when !IsTypingTarget():
                    if (_module != "production")
                    {
                        _module = "production";
                        HighlightModule();
                        ApplyModuleVisibility();
                        RefreshActiveModule();
                    }
                    GoToStage(e.Key switch { Key.D1 => "load", Key.D2 => "stock", Key.D3 => "nest", Key.D4 => "ops", _ => "out" });
                    e.Handled = true;
                    return;
            }

            if (e.Key == Key.Z)
            {
                if (_session.TryUndo())
                {
                    AfterHistoryRestore();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Y)
            {
                if (_session.TryRedo())
                {
                    AfterHistoryRestore();
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.C)
            {
                CopySelectedToClipboard();
                e.Handled = true;
            }
            else if (e.Key == Key.X)
            {
                CutSelectedPanel();
                e.Handled = true;
            }
            else if (e.Key == Key.V)
            {
                PasteClipboardPanel();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Delete)
        {
            if (SelectedFeature() is not null)
                OnGeomDeleteFeatureClick(sender, e);
            else
                OnDeletePanelClick(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.None && !IsTypingTarget())
        {
            if (!e.IsRepeat)
                CanvasHost.InvalidateVisual();
            if (_stage is "nest" or "ops" && _nestSelected.Count == 2)
                e.Handled = true;
        }
    }

    void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsAltKey(e) && _dragMode == "nest")
            ApplyNestDragAtLastPointer();
        if (IsSKey(e) && _dragMode == "nest" && !IsTypingTarget())
            ApplyNestDragAtLastPointer();
        if (e.Key == Key.D)
            CanvasHost.InvalidateVisual();
    }

    static bool IsTypingTarget() =>
        Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox;

    void AfterHistoryRestore()
    {
        InvalidateManufacturingOutputs("undo/redo");
        BindPartList(_selected?.PanelId);
        RefreshGeomRail();
        RefreshNestReport();
        UpdateCanvasHint();
        CanvasHost.InvalidateVisual();
    }

    void OnStageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, StageTabs) || _stageChanging) return;
        if (StageTabs.SelectedItem is not TabItem tab) return;
        var next = tab.Tag as string ?? "load";

        // Gate: no package → only 载入方案
        if (_session.Package is null && next != "load")
        {
            _stageChanging = true;
            StageTabs.SelectedIndex = 0;
            _stageChanging = false;
            SetStatus("请先载入方案");
            RefreshEmptyState();
            return;
        }

        _stage = next;
        if (_stage != "ops")
            ExitBridgeModes();
        if (_stage != "out")
        {
            EndSimPan();
            StopNcSim();
        }
        // Nest canvas only after an intentional nest run — blank until「初始密排」.
        _showNest = (_stage is "nest" or "ops" or "out") && _nest is { Ok: true };
        ApplyStageVisibility();
        UpdateCanvasHint();
        UpdateStageChrome();
        RefreshWorkflowDots();
        RefreshEmptyState();
        // Stock groups by material; load/nest/ops by assembly — rebind when stage changes.
        if (_session.Package is not null)
            BindPartList(null);
        CanvasHost.InvalidateVisual();

        if (_stage == "nest")
        {
            SyncNestSettingsFromPackage();
            RefreshNestReport();
            SetStatus(_nest is { Ok: true }
                ? $"密排 · 已排 {_nest.Placements.Count} 件 · {_nest.SheetCount} 张大板 · 拖动板件微调，右键改材料"
                : "密排 · 尚未排版，请先在「板材与设备」点「初始密排」", StatusKind.Info);
        }
        else if (_stage == "load")
        {
            SetStatus(_session.Package is null ? "载入方案 · 打开 .cnjob / woodjob / cut-package" : "载入方案 · 选中板件可在右侧检视和编辑", StatusKind.Info);
            RefreshGeomRail();
        }
        else if (_stage == "stock")
        {
            SyncNestSettingsFromPackage();
            RefreshStockMaterialCards();
            SetStatus("板材与设备 · 按材料种类设置大板尺寸，然后点「初始密排」", StatusKind.Info);
        }
        else if (_stage == "ops")
        {
            SetStatus(_nest is { Ok: true }
                ? "刀路 · 选机型，点右下「计算全部」；点 Profiling / Area Clearance / Drilling 查看参数"
                : "刀路 · 需要先完成密排", _nest is { Ok: true } ? StatusKind.Info : StatusKind.Warning);
        }
        else if (_stage == "out")
        {
            SetStatus(HasNcText()
                ? "导出 · 选中右侧程序文件，核对 G-code 与仿真后导出"
                : "导出 · 还没有程序文件，先在「刀路与加工档」计算刀路", HasNcText() ? StatusKind.Info : StatusKind.Warning);
        }
    }

    void ApplyStageVisibility()
    {
        var hasPkg = _session.Package is not null;
        TabStock.IsEnabled = hasPkg;
        TabNest.IsEnabled = hasPkg;
        TabOps.IsEnabled = hasPkg;
        TabOut.IsEnabled = hasPkg;

        var showGeomRail = _stage == "load" && hasPkg;
        var showStockRail = _stage == "stock";
        var showNestRail = _stage == "nest";
        GeomPane.Visibility = showGeomRail ? Visibility.Visible : Visibility.Collapsed;
        StockPane.Visibility = showStockRail ? Visibility.Visible : Visibility.Collapsed;
        NestPane.Visibility = showNestRail ? Visibility.Visible : Visibility.Collapsed;
        CanvasPane.Visibility = Visibility.Visible;

        NestPaneTitle.Text = "密排";
        NestApplyBtn.Visibility = Visibility.Visible;
        if (showStockRail)
            RefreshStockMaterialCards();
        if (showNestRail)
        {
            RefreshNestStockSummary();
            RefreshNestReport();
            UpdateNestSheetChrome();
        }

        OpsPane.Visibility = _stage == "ops" ? Visibility.Visible : Visibility.Collapsed;
        NcPane.Visibility = _stage == "out" ? Visibility.Visible : Visibility.Collapsed;
        OutGcodePane.Visibility = _stage == "out" ? Visibility.Visible : Visibility.Collapsed;
        if (OutPreviewCaption is not null)
            OutPreviewCaption.Visibility = Visibility.Collapsed;
        if (OutSimChrome is not null)
            OutSimChrome.Visibility = _stage == "out" && _nest is { Ok: true }
                ? Visibility.Visible
                : Visibility.Collapsed;

        RememberLeftRailWidth();
        var showLeftRail = _stage is not "ops" and not "out";
        LeftRail.Visibility = showLeftRail ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = _stage == "ops" ? Visibility.Collapsed : Visibility.Visible;
        StockKindPickerVisible = _stage == "stock" ? Visibility.Visible : Visibility.Collapsed;
        MergeKindsBtn.Visibility = _stage == "stock" ? Visibility.Visible : Visibility.Collapsed;
        if (CreatePanelBtn is not null)
        {
            CreatePanelBtn.Visibility = _stage == "stock" && hasPkg
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (_stage != "stock")
            _pickedStockKinds.Clear();
        SyncStockKindChecks();

        Grid.SetColumn(GeomPane, 2);
        Grid.SetColumn(StockPane, 2);
        Grid.SetColumn(NestPane, 2);
        Grid.SetColumn(OpsPane, 2);
        Grid.SetColumn(NcPane, 2);
        Grid.SetColumnSpan(GeomPane, 1);
        Grid.SetColumnSpan(StockPane, 1);
        Grid.SetColumnSpan(NestPane, 1);
        Grid.SetColumnSpan(OpsPane, 1);
        Grid.SetColumnSpan(NcPane, 1);

        if (_stage == "out")
        {
            LeftCol.MinWidth = 180;
            LeftCol.MaxWidth = 720;
            LeftCol.Width = new GridLength(1.4, GridUnitType.Star);
            NcCol.Width = new GridLength(280);
            NcPaneTitle.Text = "刀路文件";
            RefreshExportFiles();
        }
        else if (_stage == "ops")
        {
            LeftCol.MinWidth = 0;
            LeftCol.MaxWidth = double.PositiveInfinity;
            LeftCol.Width = new GridLength(0);
            ApplyOpsChrome();
            RefreshOpsRail();
        }
        else
        {
            LeftCol.MinWidth = LeftRailMinW;
            LeftCol.MaxWidth = LeftRailMaxW;
            LeftCol.Width = new GridLength(_leftRailWidth);
            NcCol.Width = new GridLength(300);
        }

        RefreshOneClickExport();
    }

    void RememberLeftRailWidth()
    {
        if (LeftRail.Visibility != Visibility.Visible) return;
        var w = LeftCol.ActualWidth;
        if (w >= LeftRailMinW && w <= LeftRailMaxW)
            _leftRailWidth = w;
    }

    void UpdateStageChrome()
    {
        StageHint.Text = _stage switch
        {
            "load" => _session.Package is null
                ? "载入方案: 打开 woodjob / cut-package，或从机台 .anc 反推补板"
                : "载入方案: 左栏 Package → Assembly → 板件 · 可用「加入方案」再并一单",
            "stock" => "板材与设备: 相同板件已合并数量 · Ctrl 点选种类后可合并 · 按材料设大板",
            "nest" => "密排: 左右翻大板 · 拖摆位 · 右键改材料 · 拖动中右键转90°",
            "ops" => "刀路: 选机器 · Profiling / Area Clearance / Drilling · 计算当前板或全部",
            "out" => "导出: 点右侧刀路文件，左边看 G-code，中间看该大板刀路",
            _ => "",
        };
        AllowOverlapChk.Visibility = _stage == "nest" ? Visibility.Visible : Visibility.Collapsed;
        LockPlaceBtn.Visibility = _stage == "nest" ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshWorkflowDots()
    {
        WfDots.Children.Clear();
        var pkg = _session.Package;
        var hasPkg = pkg?.Panels.Count > 0;
        var hasNest = _nest is { Ok: true, Placements.Count: > 0 };
        var hasOps = _opsOverlay.Count > 0 || HasNcText();
        var hasNc = HasNcText();
        var stages = new (string Id, string Label, bool Done, string Hint)[]
        {
            ("load", "载入", hasPkg, hasPkg ? "方案已载入" : "尚未载入方案"),
            ("stock", "板材", hasPkg, hasPkg ? "板材参数可用" : "先载入方案"),
            ("nest", "密排", hasNest, hasNest ? "密排完成" : "尚未密排"),
            ("ops", "刀路", hasOps, hasOps ? "刀路已计算" : "尚未计算刀路"),
            ("out", "导出", hasNc, hasNc ? "程序文件就绪" : "尚无程序文件"),
        };
        var stale = _session.ManufacturingDirty && (hasNest || hasNc);
        foreach (var (id, label, done, hint) in stages)
        {
            var current = id == _stage;
            var showStale = stale && id is "nest" or "ops" or "out";
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Background = showStale
                    ? (Brush)FindResource("WarningSoftBrush")
                    : done ? (Brush)FindResource("SuccessSoftBrush") : (Brush)FindResource("HoverBrush"),
                BorderBrush = current ? (Brush)FindResource("NavyBrush") : Brushes.Transparent,
                BorderThickness = new Thickness(current ? 1.5 : 0),
                ToolTip = showStale ? "板件已修改，需要重新密排" : hint,
                Cursor = Cursors.Hand,
                Tag = id,
            };
            var text = new StackPanel { Orientation = Orientation.Horizontal };
            text.Children.Add(new TextBlock
            {
                Text = showStale ? "\uE7BA" : done ? "\uE73E" : "\uE91F",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = showStale
                    ? (Brush)FindResource("WarningBrush")
                    : done ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("TextMutedBrush"),
            });
            text.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = done || current ? (Brush)FindResource("TextBrush") : (Brush)FindResource("TextMutedBrush"),
            });
            pill.Child = text;
            pill.MouseLeftButtonUp += (_, _) => { if (pill.Tag is string s) GoToStage(s); };
            WfDots.Children.Add(pill);
        }
        RefreshOneClickExport();
        RefreshStaleBanner();
        ApplyAwaitingNestChrome();
        ApplyProjectNameChrome();
    }

    bool HasNcText() =>
        _exportFiles.Count > 0
        || (!string.IsNullOrWhiteSpace(NcPreview.Text) && !NcPreview.Text.StartsWith("//"));

    void RefreshOneClickExport() =>
        OneClickExportBtn.IsEnabled = _nest is { Ok: true, Placements.Count: > 0 } && HasNcText();

    public sealed class ExportNcFile
    {
        public required string FileName { get; init; }
        public required string Title { get; init; }
        public required string Detail { get; init; }
        public required int SheetIndex { get; init; }
        public required NestGroupKey KindKey { get; init; }
        public required string KindLabel { get; init; }
        public required string ToolId { get; init; }
        public required string NcText { get; init; }
        public required IReadOnlyList<CutOp> Ops { get; init; }
        public IReadOnlyList<LabelPaste> Labels { get; init; } = [];
    }

    static int ExportToolRank(string? toolId) => (toolId ?? "").ToUpperInvariant() switch
    {
        "T3" => 0,
        "T1" => 1,
        "T2" => 2,
        _ => 8,
    };

    static string ExportKindLabel(IReadOnlyList<CutOp> ops)
    {
        if (ops.Count == 0) return "刀路";
        if (ops.All(o => o.Op == "drill")) return "钻孔";
        if (ops.All(o => o.Op == "groove" && o.IsTongue)) return "半槽";
        var hasContour = ops.Any(o => o.Op == "contour");
        var hasClear = ops.Any(o => o.Op == "pocket" || (o.Op == "groove" && !o.IsTongue));
        if (hasContour && hasClear) return "清底+外形";
        if (hasContour) return "外形";
        if (hasClear) return "清底";
        if (ops.Any(o => o.IsTongue)) return "半槽";
        return "刀路";
    }

    static string ExportSheetDetail(IReadOnlyList<CutOp> ops)
    {
        var tools = ops
            .Select(o => o.ToolId)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ExportToolRank)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var toolBit = tools.Count > 0 ? string.Join("+", tools) + " · " : "";
        return $"{toolBit}{ExportKindLabel(ops)} · {ops.Count} 刀";
    }

    void RefreshExportFiles()
    {
        if (OutFileList is null || NcPreview is null) return;
        var keepName = _exportSelected?.FileName;
        var keepSheet = _exportSelected?.SheetIndex;
        _exportFiles = [];
        if (_opsOverlay.Count > 0 && _nest is { Ok: true })
        {
            var profile = ActiveProfileForCam();
            var recipe = CurrentPostRecipe();
            var project = _session.ResolvedProjectName;
            var kindOrdinal = new Dictionary<NestGroupKey, int>();
            var labelPastes = _session.Package is { } pkg
                ? LabelExport.Build(pkg.Panels, CurrentNestPlacements(), CurrentLabelOverrides(), KindDisplayName)
                : [];
            foreach (var sheetGroup in _opsOverlay
                         .Where(o => o.Placed && o.Enabled)
                         .GroupBy(o => o.SheetIndex)
                         .OrderBy(g => g.Key))
            {
                var ops = sheetGroup.ToList();
                string nc;
                try
                {
                    nc = NcEmitter.OpsToNc(ops, profile, recipe: recipe);
                }
                catch (Exception ex)
                {
                    nc = "// " + ex.Message;
                }
                var sheetLabels = labelPastes.Where(p => p.SheetIndex == sheetGroup.Key).ToList();
                if (sheetLabels.Count > 0 && !nc.StartsWith("//", StringComparison.Ordinal))
                    nc = LabelExport.WrapCutWithLabelProcess(nc, LabelExport.EmitPro2(sheetLabels));
                var tools = ops
                    .Select(o => o.ToolId)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ExportToolRank)
                    .ToList();
                var detail = ExportSheetDetail(ops);
                if (sheetLabels.Count > 0)
                    detail += $" · 贴标 {sheetLabels.Count}";
                // The operator must be able to tell which post made the file: Z frame and
                // tool-change behaviour differ between the OSAI single-file post and Sheet×Tool.
                detail = $"OSAI 单文件 .anc · {(recipe.Z0IsBoardBottom ? "Z0=板底" : "Z0=板面")} · 自动换刀 M6 · 安全高 {recipe.SafeZMm:0}\n{detail}";
                var panel = PanelOnSheet(sheetGroup.Key, sheetGroup.Select(o => o.PanelId));
                var key = panel is null
                    ? NestGroupKey.From(null, sheetGroup.Key)
                    : NestGroupKey.From(panel.Material, panel.ThicknessMm);
                kindOrdinal.TryGetValue(key, out var n);
                n++;
                kindOrdinal[key] = n;
                var kindLabel = panel is null ? $"大板{sheetGroup.Key + 1}" : KindDisplayName(panel);
                var color = panel?.DisplayColor ?? "Unassigned";
                var kind = panel?.DisplayKind ?? $"Sheet{sheetGroup.Key + 1}";
                var thickness = panel?.ThicknessMm ?? 0;
                _exportFiles.Add(new ExportNcFile
                {
                    FileName = ExportNaming.AncFileName(n, thickness, color, kind, project),
                    Title = $"{n:00} · {ExportNaming.ThicknessToken(thickness)} · {color} · {kind} · {project}",
                    Detail = detail,
                    SheetIndex = sheetGroup.Key,
                    KindKey = key,
                    KindLabel = kindLabel,
                    ToolId = string.Join("+", tools),
                    NcText = nc,
                    Ops = ops,
                    Labels = sheetLabels,
                });
            }
        }

        _syncingExportFiles = true;
        OutFileList.ItemsSource = _exportFiles;
        ExportNcFile? pick = null;
        if (keepName is not null)
            pick = _exportFiles.FirstOrDefault(f => f.FileName == keepName);
        if (pick is null && keepSheet is int si)
            pick = _exportFiles.FirstOrDefault(f => f.SheetIndex == si);
        pick ??= _exportFiles.FirstOrDefault();
        OutFileList.SelectedItem = pick;
        _syncingExportFiles = false;
        ApplyExportFile(pick);
        RefreshExportButtons();
        OutOpsMeta.Text = _exportFiles.Count == 0
            ? "请先在「4 刀路与加工档」计算刀路"
            : $"{_exportFiles.Count} 张大板 · 每板一个 .anc（OSAI 单文件后置，含自动换刀）· 标签 BMP 随程序一起写出";
        RefreshPreflightMeta();
        RefreshWorkflowDots();
    }

    void OnOutFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingExportFiles) return;
        ApplyExportFile(OutFileList.SelectedItem as ExportNcFile);
        RefreshExportButtons();
    }

    void RefreshExportButtons()
    {
        if (OutExportSelectedBtn is null || OutExportAllBtn is null) return;
        OutExportSelectedBtn.IsEnabled = OutFileList.SelectedItems.Count > 0;
        OutExportAllBtn.IsEnabled = _exportFiles.Count > 0;
        if (OutExportKindBtn is not null)
            OutExportKindBtn.IsEnabled = ExportFilesOfSelectedKind().Count > 0;
        var hasNest = _nest is { Ok: true, Placements.Count: > 0 };
        if (OutExportDxfSheetBtn is not null)
            OutExportDxfSheetBtn.IsEnabled = hasNest && CurrentDxfSheetIndex() is not null;
        if (OutExportDxfKindBtn is not null)
            OutExportDxfKindBtn.IsEnabled = hasNest && DxfSheetIndexesOfSelectedKind().Count > 0;
    }

    IReadOnlyList<ExportNcFile> ExportFilesOfSelectedKind()
    {
        var seed = OutFileList?.SelectedItem as ExportNcFile ?? _exportSelected;
        if (seed is null) return [];
        return _exportFiles.Where(f => f.KindKey.Equals(seed.KindKey)).ToList();
    }

    IReadOnlyList<ExportNcFile> SelectedExportFiles()
    {
        var picked = OutFileList.SelectedItems.Cast<ExportNcFile>().ToHashSet();
        return _exportFiles.Where(picked.Contains).ToList();
    }

    void OnExportSelectedClick(object sender, RoutedEventArgs e)
    {
        var files = SelectedExportFiles();
        if (files.Count == 0)
        {
            SetStatus("请先选中刀路文件");
            return;
        }
        WriteExportNcFiles(files);
    }

    void OnExportKindClick(object sender, RoutedEventArgs e)
    {
        var files = ExportFilesOfSelectedKind();
        if (files.Count == 0)
        {
            SetStatus("请先选中一张该种类的大板");
            return;
        }
        WriteExportNcFiles(files);
    }

    void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        if (_exportFiles.Count == 0)
        {
            SetStatus("没有可导出的刀路文件");
            return;
        }
        WriteExportNcFiles(_exportFiles.ToList());
    }

    void WriteExportNcFiles(IReadOnlyList<ExportNcFile> files)
    {
        var names = files.Select(f => f.FileName).ToList();
        var snapshot = files.ToDictionary(f => f.FileName, StringComparer.Ordinal);
        if (!GuardExportPreflight(files)) return;

        var byName = _exportFiles.ToDictionary(f => f.FileName, StringComparer.Ordinal);
        var toWrite = new List<ExportNcFile>();
        foreach (var name in names)
        {
            if (byName.TryGetValue(name, out var fresh))
                toWrite.Add(fresh);
            else if (snapshot.TryGetValue(name, out var old))
                toWrite.Add(old);
        }
        toWrite = toWrite.Where(f => !string.IsNullOrWhiteSpace(f.NcText) && !f.NcText.StartsWith("//")).ToList();
        if (toWrite.Count == 0)
        {
            SetStatus("选中的文件没有可写的 G-code");
            return;
        }

        if (toWrite.Count == 1)
        {
            var one = toWrite[0];
            var dlg = new SaveFileDialog
            {
                Filter = "Troy OSAI (*.anc)|*.anc|NC (*.nc)|*.nc|All|*.*",
                FileName = one.FileName,
                Title = "导出当前选中",
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, one.NcText);
            var oneDir = Path.GetDirectoryName(dlg.FileName)!;
            var oneLabels = WriteLabelBmps(oneDir, [one]);
            SetStatus(oneLabels.Text is null
                ? $"已导出 {one.FileName} → {dlg.FileName}"
                : $"已导出 {one.FileName} · {oneLabels.Text}");
            AnnounceExport(1, one.Labels.Count, oneLabels.Missing, oneDir);
            UsageLog.LogActionResult("export.nc.selected", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["count"] = 1,
                ["path"] = dlg.FileName,
                ["file"] = one.FileName,
            });
            return;
        }

        var folder = new OpenFolderDialog { Title = "选择导出目录" };
        if (folder.ShowDialog() != true) return;
        var dir = folder.FolderName;
        foreach (var f in toWrite)
            File.WriteAllText(Path.Combine(dir, f.FileName), f.NcText);
        var manyLabels = WriteLabelBmps(dir, toWrite);
        SetStatus(manyLabels.Text is null
            ? $"已导出 {toWrite.Count} 个文件 → {dir}"
            : $"已导出 {toWrite.Count} 个文件 · {manyLabels.Text}");
        AnnounceExport(toWrite.Count, toWrite.Sum(f => f.Labels.Count), manyLabels.Missing, dir);
        UsageLog.LogActionResult("export.nc.files", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["count"] = toWrite.Count,
            ["dir"] = dir,
            ["files"] = toWrite.Select(f => f.FileName).ToArray(),
        });
    }

    /// <summary>
    /// Writes one <c>stem.bmp</c> per paste flat next to the NC, then checks that every
    /// <c>LS11</c> the programs will request has a bitmap. Returns the status text, or null
    /// when the files carry no labels. The machine's label software only searches its
    /// configured picture folder (no sub-folders), so the operator copies the bitmaps
    /// straight into <see cref="LabelerDefaults.MachinePictureDir"/>.
    /// </summary>
    (string? Text, int Missing) WriteLabelBmps(string directory, IReadOnlyList<ExportNcFile> files)
    {
        var pastes = files.SelectMany(f => f.Labels).ToList();
        if (pastes.Count == 0 || string.IsNullOrWhiteSpace(directory))
            return (null, 0);
        Directory.CreateDirectory(directory);
        foreach (var paste in pastes)
            File.WriteAllBytes(Path.Combine(directory, paste.Stem + ".bmp"), LabelBmp.Render(paste));

        var onDisk = Directory.EnumerateFiles(directory, "*.bmp")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s is not null)
            .Select(s => s!);
        var missing = files
            .SelectMany(f => LabelExport.MissingBitmaps(f.NcText, onDisk))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var target = _library.Labeler.MachinePictureDir;
        if (missing.Count > 0)
        {
            UsageLog.LogActionResult("export.labels.missing", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["dir"] = directory,
                ["missing"] = missing.ToArray(),
            });
            return ($"标签 {pastes.Count} 张在 {directory}；但程序请求的 {missing.Count} 个标签没有 BMP（{string.Join(", ", missing.Take(5))}），"
                 + "上机前必须补齐，否则 M701 会一直等待", missing.Count);
        }
        return ($"标签 {pastes.Count} 张已平铺写入 {directory}，全部复制到机床 {target}（不要放子目录）", 0);
    }

    /// <summary>One clear card after every export: what was written, where the labels go, and a way to get there.</summary>
    void AnnounceExport(int fileCount, int labelCount, int missingLabels, string dir)
    {
        if (missingLabels > 0)
        {
            ShowToast($"导出完成，但有 {missingLabels} 个标签缺少 BMP",
                "程序里的 LS11 请求了不存在的标签图片。上机前补齐，否则机床会在 M701 一直等待。",
                StatusKind.Error, "打开目录", () => OpenFolder(dir));
            return;
        }
        var detail = labelCount > 0
            ? $"{labelCount} 张标签 BMP 已平铺写在同一目录。全部复制到机床 {_library.Labeler.MachinePictureDir}，不要放子目录。"
            : "没有标签需要复制。";
        ShowToast($"已导出 {fileCount} 个程序文件", detail, StatusKind.Success, "打开目录", () => OpenFolder(dir));
    }

    void ApplyExportFile(ExportNcFile? file)
    {
        _exportSelected = file;
        if (NcPreview is not null)
            NcPreview.Text = file?.NcText ?? "";
        if (file is not null)
        {
            if (_stage == "out")
            {
                _activeNestSheet = file.SheetIndex;
                UpdateNestSheetChrome();
                SetStatus($"导出 · {file.Title}");
            }
        }
        if (OutPreviewCaption is not null)
            OutPreviewCaption.Visibility = Visibility.Collapsed;
        LoadNcSim(file);
        CanvasHost.InvalidateVisual();
    }

    void LoadNcSim(ExportNcFile? file)
    {
        StopNcSim();
        ResetSimView();
        _ncSimTime = 0;
        _ncSimTotal = 0;
        _ncSimStrokes = [];
        _ncSimStarts = [];
        if (GcodeHeader is not null) GcodeHeader.Text = "G-code · 点任意行可定位仿真";
        _ncHighlightLine = -1;
        PositionNcHighlight();
        var text = file?.NcText;
        if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("//", StringComparison.Ordinal))
        {
            try
            {
                _ncSimStrokes = OsaiTroyParser.Replay(text).Strokes;
                _ncSimTotal = NcCutSim.TotalSec(_ncSimStrokes);
                var starts = new List<double>(_ncSimStrokes.Count);
                var acc = 0d;
                foreach (var s in _ncSimStrokes)
                {
                    starts.Add(acc);
                    acc += NcCutSim.DurationSec(s);
                }
                _ncSimStarts = starts;
            }
            catch
            {
                _ncSimStrokes = [];
                _ncSimTotal = 0;
                _ncSimStarts = [];
            }
        }
        UpdateOutSimChrome();
    }

    List<double> _ncSimStarts = [];
    bool _syncingNcLine;

    /// <summary>Jump the simulation to the start of stroke <paramref name="index"/> (clamped).</summary>
    void SeekNcSimToStroke(int index)
    {
        if (_ncSimStarts.Count == 0) return;
        index = Math.Clamp(index, 0, _ncSimStarts.Count - 1);
        _ncSimTime = _ncSimStarts[index];
        UpdateOutSimChrome();
        CanvasHost.InvalidateVisual();
    }

    void OnOutSimToStartClick(object sender, RoutedEventArgs e)
    {
        StopNcSim();
        _ncSimTime = 0;
        UpdateOutSimChrome();
        CanvasHost.InvalidateVisual();
    }

    void OnOutSimToEndClick(object sender, RoutedEventArgs e)
    {
        StopNcSim();
        _ncSimTime = _ncSimTotal;
        UpdateOutSimChrome();
        CanvasHost.InvalidateVisual();
    }

    void OnOutSimStepBackClick(object sender, RoutedEventArgs e)
    {
        if (_ncSimStrokes.Count == 0) return;
        StopNcSim();
        var pose = NcCutSim.At(_ncSimStrokes, _ncSimTime);
        // Mid-stroke → back to this stroke's start; at a start → previous stroke.
        var idx = pose.StrokeIndex < 0 ? 0 : pose.StrokeIndex;
        if (idx < _ncSimStarts.Count && _ncSimTime <= _ncSimStarts[idx] + 1e-6)
            idx--;
        SeekNcSimToStroke(idx);
    }

    void OnOutSimStepForwardClick(object sender, RoutedEventArgs e)
    {
        if (_ncSimStrokes.Count == 0) return;
        StopNcSim();
        var pose = NcCutSim.At(_ncSimStrokes, _ncSimTime);
        var idx = (pose.StrokeIndex < 0 ? 0 : pose.StrokeIndex) + 1;
        if (idx >= _ncSimStarts.Count)
        {
            _ncSimTime = _ncSimTotal;
            UpdateOutSimChrome();
            CanvasHost.InvalidateVisual();
            return;
        }
        SeekNcSimToStroke(idx);
    }

    int _ncHighlightLine = -1;

    /// <summary>
    /// Backplot → code: mark the G-code block the cutter is executing and keep it in view.
    /// A translucent overlay is used instead of the TextBox selection so playback never
    /// steals a selection the operator made to copy code, and the marker stays visible
    /// while the box is unfocused.
    /// </summary>
    void HighlightNcLine(int line)
    {
        if (NcPreview is null || line < 0 || _syncingNcLine) return;
        _ncHighlightLine = line;
        try
        {
            var start = NcPreview.GetCharacterIndexFromLineIndex(line);
            if (start < 0) return;
            var rect = NcPreview.GetRectFromCharacterIndex(start);
            if (rect.IsEmpty) return;
            _syncingNcLine = true;
            // Keep the executing block in the upper third, the way NC viewers do.
            var viewport = NcPreview.ViewportHeight;
            if (viewport > 0 && (rect.Top < 0 || rect.Bottom > viewport))
                NcPreview.ScrollToVerticalOffset(Math.Max(0, NcPreview.VerticalOffset + rect.Top - viewport * 0.35));
            PositionNcHighlight();
            if (GcodeHeader is not null)
                GcodeHeader.Text = $"G-code · 执行到第 {line + 1} 行 · 点任意行可定位仿真";
        }
        catch
        {
            // TextBox not laid out yet — the next tick will retry.
        }
        finally
        {
            _syncingNcLine = false;
        }
    }

    /// <summary>Re-place the marker after scrolling or a new highlight; hides it when off-screen.</summary>
    void PositionNcHighlight()
    {
        if (NcLineHighlight is null || NcPreview is null) return;
        if (_ncHighlightLine < 0 || _stage != "out")
        {
            NcLineHighlight.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var start = NcPreview.GetCharacterIndexFromLineIndex(_ncHighlightLine);
            if (start < 0) { NcLineHighlight.Visibility = Visibility.Collapsed; return; }
            var rect = NcPreview.GetRectFromCharacterIndex(start);
            var viewport = NcPreview.ViewportHeight;
            if (rect.IsEmpty || rect.Bottom < 0 || (viewport > 0 && rect.Top > viewport))
            {
                NcLineHighlight.Visibility = Visibility.Collapsed;
                return;
            }
            NcLineHighlight.Margin = new Thickness(0, Math.Max(0, rect.Top), 12, 0);
            NcLineHighlight.Height = Math.Max(2, rect.Height);
            NcLineHighlight.Visibility = Visibility.Visible;
        }
        catch
        {
            NcLineHighlight.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Code → backplot: clicking a G-code line seeks the simulation to that block.</summary>
    void OnNcPreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (_stage != "out" || _ncSimStrokes.Count == 0 || _syncingNcLine) return;
        var caret = NcPreview.CaretIndex;
        var line = NcPreview.GetLineIndexFromCharacterIndex(caret);
        if (line < 0) return;
        var idx = -1;
        for (var i = 0; i < _ncSimStrokes.Count; i++)
        {
            if (_ncSimStrokes[i].LineIndex >= line)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0)
        {
            SetStatus("这一行没有刀具运动；已定位到最近的运动块", StatusKind.Info);
            idx = _ncSimStrokes.Count - 1;
        }
        StopNcSim();
        SeekNcSimToStroke(idx);
    }

    void StopNcSim()
    {
        _ncSimPlaying = false;
        _ncSimTimer.Stop();
        if (OutSimPlayBtn is not null)
            OutSimPlayBtn.Content = "▶";
    }

    void TickNcSim()
    {
        if (!_ncSimPlaying || _ncSimTotal <= 0)
        {
            StopNcSim();
            return;
        }
        _ncSimTime += _ncSimTimer.Interval.TotalSeconds * _ncSimSpeed;
        if (_ncSimTime >= _ncSimTotal)
        {
            _ncSimTime = _ncSimTotal;
            StopNcSim();
        }
        UpdateOutSimChrome();
        CanvasHost.InvalidateVisual();
    }

    void OnOutSimPlayClick(object sender, RoutedEventArgs e)
    {
        if (_ncSimStrokes.Count == 0 || _ncSimTotal <= 0)
            return;
        if (_ncSimPlaying)
        {
            StopNcSim();
            return;
        }
        if (_ncSimTime >= _ncSimTotal - 1e-6)
            _ncSimTime = 0;
        _ncSimPlaying = true;
        if (OutSimPlayBtn is not null)
            OutSimPlayBtn.Content = "❚❚";
        _ncSimTimer.Start();
        UpdateOutSimChrome();
    }

    void OnOutSimSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutSimSpeed?.SelectedItem is ComboBoxItem { Tag: string tag }
            && double.TryParse(tag, out var speed)
            && speed > 0)
            _ncSimSpeed = speed;
    }

    void OnOutSimSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingNcSimSlider || _ncSimTotal <= 0) return;
        _ncSimTime = OutSimSlider.Value / 1000.0 * _ncSimTotal;
        if (_ncSimPlaying && _ncSimTime >= _ncSimTotal)
            StopNcSim();
        UpdateOutSimChrome();
        CanvasHost.InvalidateVisual();
    }

    void UpdateOutSimChrome()
    {
        if (OutSimChrome is null) return;
        OutSimChrome.Visibility = _stage == "out" && _nest is { Ok: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (OutSimTitle is not null)
            OutSimTitle.Text = _exportSelected is { } f
                ? $"仿真 · {f.Title}"
                : "G-code 切割仿真";
        if (OutSimMeta is not null)
        {
            if (_ncSimStrokes.Count == 0)
            {
                OutSimMeta.Text = _exportSelected is null
                    ? "选择右侧程序开始仿真"
                    : "无法解析该程序";
            }
            else
            {
                var pose = NcCutSim.At(_ncSimStrokes, _ncSimTime);
                var shop = ShopToolDiaByNum();
                var dia = NcCutSim.ToolDiameterMm(pose.ToolNum, shop);
                if (pose.StrokeIndex < 0)
                {
                    OutSimMeta.Text = "线宽=刀径 · 滚轮缩放 · 中键平移 · F 适配";
                }
                else
                {
                    // DRO-style readout, the way a controller shows it: position, tool, feed, block.
                    var stroke = _ncSimStrokes[pose.StrokeIndex];
                    OutSimMeta.Text =
                        $"X{pose.X,7:0.0} Y{pose.Y,7:0.0} Z{pose.Z,6:0.00}  T{pose.ToolNum} Ø{dia:0.#}  " +
                        (pose.Rapid ? "快移" : $"F{pose.Feed:0}") +
                        $"  {pose.StrokeIndex + 1}/{_ncSimStrokes.Count}";
                    HighlightNcLine(stroke.LineIndex);
                }
            }
        }
        if (OutSimTime is not null)
            OutSimTime.Text = $"{FmtSimClock(_ncSimTime)} / {FmtSimClock(_ncSimTotal)}";
        if (OutSimSlider is not null)
        {
            _syncingNcSimSlider = true;
            OutSimSlider.IsEnabled = _ncSimTotal > 0;
            OutSimSlider.Value = _ncSimTotal > 0
                ? Math.Clamp(_ncSimTime / _ncSimTotal * 1000, 0, 1000)
                : 0;
            _syncingNcSimSlider = false;
        }
        if (OutSimPlayBtn is not null)
        {
            OutSimPlayBtn.IsEnabled = _ncSimTotal > 0;
            if (!_ncSimPlaying)
                OutSimPlayBtn.Content = "▶";
        }
    }

    void ResetSimView()
    {
        _simUserScale = 0;
        _simOx = _simOy = 0;
        _simPanning = false;
    }

    void OnOutSimResetViewClick(object sender, RoutedEventArgs e)
    {
        ResetSimView();
        CanvasHost.InvalidateVisual();
    }

    (float Scale, float Ox, float Oy) ResolveSimView(float fitScale, float pad)
    {
        if (_simUserScale <= 0)
            return (fitScale, pad, pad);
        return (_simUserScale, _simOx, _simOy);
    }

    void CommitSimView(float scale, float ox, float oy)
    {
        _simUserScale = scale;
        _simOx = ox;
        _simOy = oy;
    }

    /// <summary>The sheet viewport is live on the nest, ops and export stages once a nest exists.</summary>
    bool ViewportActive() => _showNest && _stage is "nest" or "ops" or "out" && _nest is { Ok: true };

    /// <summary>
    /// Fit scale and padding exactly as OnPaintSurface computes them, so wheel zoom, pan and
    /// the zoom readout agree with what is drawn (the nest stage reserves the holding bay).
    /// </summary>
    (float Fit, float Pad) CurrentNestFit()
    {
        var (sw, sh, _) = ActiveSheetMetrics();
        var w = _surfaceW > 0 ? _surfaceW : (float)(CanvasHost.ActualWidth * _dpiX);
        var h = _surfaceH > 0 ? _surfaceH : (float)(CanvasHost.ActualHeight * _dpiY);
        var bay = _stage == "nest" ? CanvasPainter.NestHoldingBayWidth : 0f;
        var pad = _stage == "out" ? 56f : 44f;
        if (sw <= 0 || sh <= 0) return (0, pad);
        var availW = Math.Max(1f, w - bay - pad);
        var fit = Math.Min(availW / sw, (h - 2 * pad) / sh) * 0.9f;
        return (fit > 0 ? fit : 0, pad);
    }

    void ZoomViewportAt(float sx, float sy, double factor)
    {
        var (fit, pad) = CurrentNestFit();
        if (fit <= 0) return;
        var (_, sh, _) = ActiveSheetMetrics();
        var (scale, ox, oy) = ResolveSimView(fit, pad);
        var wx = (sx - ox) / scale;
        var wy = sh - (sy - oy) / scale;
        var next = (float)Math.Clamp(scale * factor, fit * 0.05, fit * 80);
        CommitSimView(next, sx - wx * next, sy - (sh - wy) * next);
        CanvasHost.InvalidateVisual();
        UpdateViewportReadout();
    }

    void ZoomViewportCentered(double factor)
    {
        if (!ViewportActive()) return;
        RefreshDpi();
        var w = _surfaceW > 0 ? _surfaceW : (float)(CanvasHost.ActualWidth * _dpiX);
        var h = _surfaceH > 0 ? _surfaceH : (float)(CanvasHost.ActualHeight * _dpiY);
        var bay = _stage == "nest" ? CanvasPainter.NestHoldingBayWidth : 0f;
        ZoomViewportAt((w - bay) * 0.5f, h * 0.5f, factor);
    }

    void FitViewport()
    {
        ResetSimView();
        CanvasHost.InvalidateVisual();
        UpdateViewportReadout();
    }

    void OnViewZoomInClick(object sender, RoutedEventArgs e) => ZoomViewportCentered(1.25);
    void OnViewZoomOutClick(object sender, RoutedEventArgs e) => ZoomViewportCentered(1 / 1.25);
    void OnViewFitClick(object sender, RoutedEventArgs e) => FitViewport();

    /// <summary>Status-bar readout: cursor position in sheet millimetres and zoom relative to fit.</summary>
    void UpdateViewportReadout(float? sx = null, float? sy = null)
    {
        if (CursorReadout is null) return;
        if (!ViewportActive() || _nestScale <= 0)
        {
            CursorReadout.Text = "";
            return;
        }
        var (fit, _) = CurrentNestFit();
        var zoom = fit > 0 ? _nestScale / fit * 100 : 100;
        var zoomText = $"缩放 {zoom:0}%";
        ViewportZoomText.Text = zoomText;
        if (sx is float x && sy is float y && (_stage != "nest" || _holdingBayLeft <= 0 || x < _holdingBayLeft))
        {
            var (mx, my) = ScreenToSheet(x, y);
            CursorReadout.Text = $"X {mx:0.0}  Y {my:0.0} mm  ·  {zoomText}";
        }
        else
        {
            CursorReadout.Text = zoomText;
        }
    }

    void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (!ViewportActive()) return;
        if (IsNestChromeClick(e.OriginalSource)) return;
        RefreshDpi();
        var (sx, sy) = CanvasPixelPos(e);
        if (_stage == "nest" && _holdingBayLeft > 0 && sx >= _holdingBayLeft) return;
        var steps = e.Delta / 120.0;
        ZoomViewportAt(sx, sy, Math.Pow(1.2, steps));
        e.Handled = true;
    }

    void OnCanvasPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!ViewportActive()) return;
        if (IsNestChromeClick(e.OriginalSource)) return;
        RefreshDpi();
        var (x, y) = CanvasPixelPos(e);
        if (e.ClickCount >= 2)
        {
            ResetSimView();
            CanvasHost.InvalidateVisual();
            e.Handled = true;
            return;
        }
        BeginSimPan(x, y);
        CanvasPane.CaptureMouse();
        CanvasPane.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    void OnCanvasPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (!_simPanning) return;
        EndSimPan();
        e.Handled = true;
    }

    void BeginSimPan(float x, float y)
    {
        var (fit, pad) = CurrentNestFit();
        if (fit <= 0) return;
        var (scale, ox, oy) = ResolveSimView(fit, pad);
        CommitSimView(scale, ox, oy);
        _simPanning = true;
        _simPanStartX = x;
        _simPanStartY = y;
        _simPanOrigX = ox;
        _simPanOrigY = oy;
    }

    void EndSimPan()
    {
        if (!_simPanning) return;
        _simPanning = false;
        if (CanvasPane.IsMouseCaptured && _dragMode is null)
            CanvasPane.ReleaseMouseCapture();
        CanvasPane.Cursor = Cursors.Arrow;
    }

    static string FmtSimClock(double sec)
    {
        if (sec < 60)
            return $"{Math.Max(0, sec):0.0}s";
        var m = (int)(sec / 60);
        var s = sec - m * 60;
        return $"{m}:{s:00.0}";
    }

    void RefreshEmptyState()
    {
        var empty = _session.Package is null && _stage == "load" && _module == "production";
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>密排/刀路页在尚未成功排版前：左栏与画布保持空白。</summary>
    bool AwaitingInitialNest() =>
        (_stage is "nest" or "ops") && _nest is not { Ok: true };

    void UpdateCanvasHint()
    {
        CanvasHint.Visibility = Visibility.Collapsed;
        CanvasHint.Text = "";
        ApplyAwaitingNestChrome();
    }

    void ApplyAwaitingNestChrome()
    {
        var awaiting = AwaitingInitialNest();
        LeftRailContent.Visibility = awaiting ? Visibility.Collapsed : Visibility.Visible;
        NestAwaitingState.Visibility = awaiting && _stage == "nest"
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpsAwaitingState.Visibility = awaiting && _stage == "ops"
            ? Visibility.Visible
            : Visibility.Collapsed;
        var outAwaiting = _stage == "out" && _session.Package is not null && !HasNcText();
        if (outAwaiting)
        {
            var noNest = _nest is not { Ok: true };
            OutAwaitingText.Text = noNest
                ? "导出需要先完成「3 密排」和「4 刀路与加工档」。"
                : "到「4 刀路与加工档」点「计算全部」生成刀路后，这里会按大板列出可导出的程序文件。";
            OutAwaitingBtn.Content = noNest ? "前往密排" : "前往刀路";
            OutAwaitingBtn.Tag = noNest ? "nest" : "ops";
        }
        OutAwaitingState.Visibility = outAwaiting ? Visibility.Visible : Visibility.Collapsed;
        if (ViewportTools is not null)
        {
            ViewportTools.Visibility = ViewportActive() ? Visibility.Visible : Visibility.Collapsed;
            UpdateViewportReadout();
        }
        NestCanvasChrome.Visibility = !awaiting && _stage is "nest" or "ops" && _nest is { Ok: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (awaiting)
        {
            PartList.ItemsSource = null;
            PartList.SelectedItem = null;
            _selected = null;
        }
        UpdateNestSheetChrome();
    }

    void OnGoStockForNestClick(object sender, RoutedEventArgs e)
    {
        GoToStage("stock");
        SetStatus("板材与设备 · 确认参数后点「初始密排」");
    }

    /// <summary>Programmatic stage switch through the tab control so OnStageChanged does the bookkeeping.</summary>
    void GoToStage(string stage)
    {
        var index = stage switch { "load" => 0, "stock" => 1, "nest" => 2, "ops" => 3, "out" => 4, _ => 0 };
        if (index > 0 && _session.Package is null) index = 0;
        if (StageTabs.SelectedIndex == index)
        {
            _stage = stage;
            ApplyStageVisibility();
            UpdateCanvasHint();
            UpdateStageChrome();
            RefreshWorkflowDots();
            RefreshEmptyState();
            CanvasHost.InvalidateVisual();
            return;
        }
        StageTabs.SelectedIndex = index;
    }

    void OnStaleGotoNestClick(object sender, RoutedEventArgs e) => GoToStage("nest");

    void OnGotoOpsClick(object sender, RoutedEventArgs e) =>
        GoToStage(sender is Button { Tag: "nest" } ? "nest" : "ops");

    async void OnStaleRenestClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null) return;
        GoToStage("nest");
        SetStatus("重新密排中…", StatusKind.Info);
        await RunNestAsync(withNc: false);
    }

    void OnMoreMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    void OnExitClick(object sender, RoutedEventArgs e) => Close();

    void OnMenuUndoClick(object sender, RoutedEventArgs e)
    {
        if (_session.TryUndo()) AfterHistoryRestore();
        else SetStatus("没有可撤销的操作", StatusKind.Info);
    }

    void OnMenuRedoClick(object sender, RoutedEventArgs e)
    {
        if (_session.TryRedo()) AfterHistoryRestore();
        else SetStatus("没有可重做的操作", StatusKind.Info);
    }

    void OnStageMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string stage }) return;
        if (_module != "production")
        {
            _module = "production";
            HighlightModule();
            ApplyModuleVisibility();
            RefreshActiveModule();
        }
        GoToStage(stage);
    }

    void OnHelpChecklistClick(object sender, RoutedEventArgs e)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        // Packed builds ship docs next to the exe; source builds have them under the repo.
        var candidates = new[]
        {
            Path.Combine(dir, "docs", "sprint"),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "..", "docs", "sprint")),
        };
        var found = candidates.FirstOrDefault(Directory.Exists);
        if (found is null)
        {
            SetStatus("未找到 docs/sprint 目录；检查单见仓库 docs/sprint/MACHINE_DRYRUN_CHECKLIST.md", StatusKind.Warning);
            return;
        }
        OpenFolder(found);
        SetStatus($"上机检查单 MACHINE_DRYRUN_CHECKLIST.md 与后处理变更检查单在 {found}", StatusKind.Info);
    }

    void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow(SelectedMachineId(), WorkshopLibraryStore.DefaultPath()) { Owner = this };
        dlg.ShowDialog();
    }

    void OnShortcutsClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Ctrl+O　打开方案\n" +
            "Ctrl+Shift+O　打开工程\n" +
            "Ctrl+S　保存工程\n" +
            "Ctrl+E　一键导出（刀路就绪时）\n" +
            "Ctrl+1 … Ctrl+5　切换到第 1–5 步\n" +
            "Ctrl+Z / Ctrl+Y　撤销 / 重做\n" +
            "Ctrl+C / X / V　复制 / 剪切 / 粘贴板件\n" +
            "Delete　删除选中特征或整板\n" +
            "\n视口（密排 / 刀路 / 导出）\n" +
            "滚轮　对准指针缩放 · 中键拖动　平移 · 双击中键 / F / Home　适配整板 · + / −　缩放\n" +
            "密排拖动中：右键转 90° · 按住 S 或 Alt 吸附 · 选中两块板按住 D 量距\n" +
            "\n仿真（导出页）\n" +
            "空格　播放 / 暂停 · 点 G-code 行　定位到该运动块\n" +
            "\n数值框：回车应用 · Esc 离开输入框",
            "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    async void OnStockInitialNestClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }
        SetStatus("初始密排中…");
        await RunNestAsync(withNc: false);
    }

    void OnNestSheetPrevClick(object sender, RoutedEventArgs e)
    {
        if (_activeNestSheet <= 0) return;
        _activeNestSheet--;
        ResetSimView();
        UpdateNestSheetChrome();
        if (_stage == "ops" && !_opsAllSheets && _opsOverlay.Count > 0)
            RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
    }

    void OnNestSheetNextClick(object sender, RoutedEventArgs e)
    {
        var max = NestSheetCount() - 1;
        if (_activeNestSheet >= max) return;
        _activeNestSheet++;
        ResetSimView();
        UpdateNestSheetChrome();
        if (_stage == "ops" && !_opsAllSheets && _opsOverlay.Count > 0)
            RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
    }

    int NestSheetCount()
    {
        if (_nestSheetsUsed.Count > 0) return _nestSheetsUsed.Count;
        if (_nest is { Ok: true, SheetCount: > 0 }) return _nest.SheetCount;
        return 1;
    }

    void UpdateNestSheetChrome()
    {
        if (NestSheetLabel is null) return;
        var total = Math.Max(1, NestSheetCount());
        _activeNestSheet = Math.Clamp(_activeNestSheet, 0, total - 1);
        NestSheetLabel.Text = $"大板 {_activeNestSheet + 1} / {total}";
        NestSheetPrevBtn.IsEnabled = _activeNestSheet > 0;
        NestSheetNextBtn.IsEnabled = _activeNestSheet < total - 1;
        var (sw, sh, label) = ActiveSheetMetrics();
        NestSheetMeta.Text = string.IsNullOrWhiteSpace(label)
            ? $"{sw:0.#} × {sh:0.#} mm"
            : $"{label} · {sw:0.#} × {sh:0.#} mm";
    }

    (float Width, float Length, string Label) ActiveSheetMetrics()
    {
        if (_activeNestSheet >= 0 && _activeNestSheet < _nestSheetsUsed.Count)
        {
            var s = _nestSheetsUsed[_activeNestSheet];
            return ((float)s.WidthMm, (float)s.LengthMm, s.Label ?? s.Material ?? "");
        }
        var sheet = _session.Package?.Sheets.FirstOrDefault();
        var w = (float)ParseMm(StockWidthBox.Text, sheet?.WidthMm > 0 ? sheet.WidthMm : 1200);
        var h = (float)ParseMm(StockLengthBox.Text, sheet?.LengthMm > 0 ? sheet.LengthMm : 2400);
        return (w, h, "");
    }

    void RefreshNestStockSummary()
    {
        if (NestStockSummary is null) return;
        if (_stockKinds.Count == 0)
            RefreshStockMaterialCards();
        if (_stockKinds.Count == 0)
        {
            NestStockSummary.Text = "尚未载入材料种类 — 请回「板材与设备」";
            return;
        }
        NestStockSummary.Text = string.Join("\n", _stockKinds.Select(k =>
            $"{k.Label}\n  {k.WidthMm:0.#}×{k.LengthMm:0.#} · 间距 {k.SpacingMm:0.#} · 边距 {k.BorderMm:0.#}" +
            (k.AllowRotate90 ? " · 可转90°" : " · 禁转") +
            (k.AllowPartsInPart ? " · PIP" : "") +
            (k.HasLeftoverSheet
                ? $" · leftover {k.LeftoverXMm:0.#}×{k.LeftoverYMm:0.#} @0"
                : "")));
    }

    void OnModuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _module = tag;
        HighlightModule();
        ApplyModuleVisibility();
        RefreshActiveModule();
        RefreshEmptyState();
    }

    void RefreshActiveModule()
    {
        switch (_module)
        {
            case "remnants": RefreshRemnantsModule(); break;
            case "equipment": RefreshEquipmentModule(); break;
            case "routes": RefreshRoutesModule(); break;
            case "materials": RefreshMaterialsModule(); break;
            case "process": RefreshProcessModule(); break;
            case "settings": RefreshSettingsModule(); break;
        }
    }

    void HighlightModule()
    {
        void Style(Button b, bool on)
        {
            b.Background = on ? new SolidColorBrush(Color.FromRgb(0x2E, 0x4A, 0x6E)) : Brushes.Transparent;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            b.BorderThickness = new Thickness(on ? 3 : 0, 0, 0, 0);
            b.Foreground = on ? Brushes.White : (Brush)FindResource("TextOnDarkBrush");
        }
        ModuleSubtitle.Text = "切割站 · " + (_module switch
        {
            "production" => "生产加工",
            "remnants" => "补板库",
            "equipment" => "设备管理",
            "routes" => "路线管理",
            "materials" => "原料管理",
            "process" => "工艺模版",
            "settings" => "参数设置",
            _ => "生产加工",
        });
        Style(ModProductionBtn, _module == "production");
        Style(ModRemnantsBtn, _module == "remnants");
        Style(ModEquipmentBtn, _module == "equipment");
        Style(ModRoutesBtn, _module == "routes");
        Style(ModMaterialsBtn, _module == "materials");
        Style(ModProcessBtn, _module == "process");
        Style(ModSettingsBtn, _module == "settings");
    }

    void ApplyModuleVisibility()
    {
        ProductionHost.Visibility = _module == "production" ? Visibility.Visible : Visibility.Collapsed;
        RemnantsHost.Visibility = _module == "remnants" ? Visibility.Visible : Visibility.Collapsed;
        EquipmentHost.Visibility = _module == "equipment" ? Visibility.Visible : Visibility.Collapsed;
        RoutesHost.Visibility = _module == "routes" ? Visibility.Visible : Visibility.Collapsed;
        MaterialsHost.Visibility = _module == "materials" ? Visibility.Visible : Visibility.Collapsed;
        ProcessHost.Visibility = _module == "process" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = _module == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    void PersistLibrary()
    {
        WorkshopLibraryStore.Save(_library);
        SetStatus($"库已保存 · {WorkshopLibraryStore.DefaultPath()}");
    }

    void OnLibrarySaveClick(object sender, RoutedEventArgs e) => PersistLibrary();

    void OnGotoProductionClick(object sender, RoutedEventArgs e)
    {
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        RefreshEmptyState();
    }

    // ----- 补板库 -----
    void RefreshRemnantsModule()
    {
        RemnantsList.Items.Clear();
        foreach (var r in _library.Remnants)
            RemnantsList.Items.Add(
                $"{(r.UseInNest ? "[Nest]" : "[—]")} {r.Id} · {r.WidthMm:0.#}x{r.LengthMm:0.#}x{r.ThicknessMm:0.#} · {r.Material ?? "—"} · {r.Note ?? ""}");
        RemnantsMeta.Text =
            $"补板 {_library.Remnants.Count} · 参与密排 {_library.Remnants.Count(x => x.UseInNest)} · 库 {WorkshopLibraryStore.DefaultPath()}";
        RefreshRecutPanelList();
    }

    void RefreshRecutPanelList()
    {
        if (RecutPanelList is null) return;
        var keep = RecutPanelList.Items.OfType<RecutRow>().ToDictionary(r => r.PanelId, r => r.Selected);
        var rows = new List<RecutRow>();
        if (_session.Package is { Panels.Count: > 0 } pkg)
        {
            foreach (var p in pkg.Panels)
            {
                keep.TryGetValue(p.PanelId, out var on);
                if (!keep.ContainsKey(p.PanelId)) on = true;
                rows.Add(new RecutRow
                {
                    PanelId = p.PanelId,
                    Label = $"{p.DisplayTitle}  {p.DisplayDetail}",
                    Selected = on,
                });
            }
        }
        RecutPanelList.ItemsSource = rows;
    }

    sealed class RecutRow
    {
        public required string PanelId { get; init; }
        public required string Label { get; init; }
        public bool Selected { get; set; } = true;
    }

    void OnRemnantToggleNestClick(object sender, RoutedEventArgs e)
    {
        var i = RemnantsList.SelectedIndex;
        if (i < 0 || i >= _library.Remnants.Count) return;
        _library.Remnants[i].UseInNest = !_library.Remnants[i].UseInNest;
        PersistLibrary();
        RefreshRemnantsModule();
    }

    void OnRemnantAddClick(object sender, RoutedEventArgs e)
    {
        var w = ParseMm(RemWBox.Text, 0);
        var l = ParseMm(RemLBox.Text, 0);
        var t = ParseMm(RemTBox.Text, 18);
        if (w <= 0 || l <= 0)
        {
            SetStatus("补板宽/长须 > 0");
            return;
        }
        _library.Remnants.Add(new LibRemnant
        {
            Id = "REM-" + DateTime.Now.ToString("HHmmss"),
            WidthMm = w,
            LengthMm = l,
            ThicknessMm = t,
            Material = string.IsNullOrWhiteSpace(RemMatBox.Text) ? null : RemMatBox.Text.Trim(),
        });
        PersistLibrary();
        RefreshRemnantsModule();
    }

    void OnRemnantFromSheetClick(object sender, RoutedEventArgs e)
    {
        var sheet = _session.Package?.Sheets.FirstOrDefault();
        RemWBox.Text = (sheet?.WidthMm > 0 ? sheet.WidthMm : _library.Nest.DefaultSheetWidthMm).ToString("0.###");
        RemLBox.Text = (sheet?.LengthMm > 0 ? sheet.LengthMm / 2 : _library.Nest.DefaultSheetLengthMm / 2).ToString("0.###");
        RemTBox.Text = (sheet?.ThicknessMm > 0 ? sheet.ThicknessMm : 18).ToString("0.###");
        RemMatBox.Text = sheet?.Material ?? "";
        SetStatus("已填入当前板材半长作为补板草稿 — 点「添加补板」确认");
    }

    void OnRemnantDeleteClick(object sender, RoutedEventArgs e)
    {
        var i = RemnantsList.SelectedIndex;
        if (i < 0 || i >= _library.Remnants.Count) return;
        _library.Remnants.RemoveAt(i);
        PersistLibrary();
        RefreshRemnantsModule();
    }

    // ----- 设备管理 -----
    void RefreshEquipmentModule()
    {
        EquipmentList.Items.Clear();
        foreach (var m in MachineCatalog.All)
            EquipmentList.Items.Add(m);
        EquipmentList.DisplayMemberPath = "Name";
        MachineComboModule.SelectedValue = SelectedMachineId();
        var cur = MachineCatalog.Get(SelectedMachineId());
        EquipmentDetail.Text = FormatMachine(cur);
        var idx = MachineCatalog.All.ToList().FindIndex(m => m.Id == cur.Id);
        if (idx >= 0) EquipmentList.SelectedIndex = idx;
    }

    static string FormatMachine(MachineProfile m) =>
        $"id: {m.Id}\nname: {m.Name}\ndialect: {m.Dialect}\nprogramEnd: {m.ProgramEnd}\n" +
        $"toolØ: {m.ToolDiameterMm} mm\nfeedXY: {m.FeedXyMmMin}\nfeedZ: {m.FeedZMmMin}\nrpm: {m.SpindleRpm}\n" +
        $"safeZ: {m.SafeZMm}\ncontour: {m.EnableContour}  drill: {m.EnableDrill}  groove: {m.EnableGroove}\n" +
        $"origin: {m.OriginNote ?? "—"}";

    void OnEquipmentListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EquipmentList.SelectedItem is not MachineProfile m) return;
        EquipmentDetail.Text = FormatMachine(m);
        MachineComboModule.SelectedValue = m.Id;
    }

    void OnEquipmentApplyClick(object sender, RoutedEventArgs e)
    {
        if (MachineComboModule.SelectedValue is string id)
        {
            SyncMachineSelection(id);
            SetStatus($"已应用机型 · {id}");
        }
    }

    void OnMachineModuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachineComboModule.SelectedValue is string id)
            SyncMachineSelection(id);
    }

    // ----- 路线管理 -----
    void RefreshRoutesModule()
    {
        var hasPkg = _session.Package is not null;
        var hasNest = _nest is { Ok: true, Placements.Count: > 0 };
        var hasNc = HasNcText();
        RoutesMeta.Text =
            $"作业: {(hasPkg ? "已载入" : "未载入")}\n" +
            $"1 载入方案 …… {(hasPkg ? "✓" : "○")}\n" +
            $"2 板材与设备 … {(hasPkg ? "✓" : "○")}\n" +
            $"3 密排 ………… {(hasNest ? "✓" : "○")}\n" +
            $"4 刀路与加工档 {(hasNc || _opsOverlay.Count > 0 ? "✓" : "○")}\n" +
            $"5 导出 ………… {(hasNc ? "✓" : "○")}\n" +
            $"机型: {SelectedMachineId()}";
        RouteTongueChk.IsChecked = _enableTongue;
        RouteContourChk.IsChecked = _enableProfile && _enableProfileLast;
        RouteDrillChk.IsChecked = _enableDrilling;
        RouteGrooveChk.IsChecked = _enableClearance;
        RebuildOpsOverlay();
        RoutesOpsList.Items.Clear();
        if (_opsOverlay.Count == 0)
            RoutesOpsList.Items.Add("无工序 — 先密排，再在刀路页按规则计算");
        else
        {
            RoutesOpsList.Items.Add($"T1 半槽 × {_opsOverlay.Count(o => TroyPass.InPass(o, TroyPassKind.TongueGroove))}");
            RoutesOpsList.Items.Add($"T2 清底 × {_opsOverlay.Count(o => TroyPass.InPass(o, TroyPassKind.Clearance))}");
            RoutesOpsList.Items.Add($"T2 外形 × {_opsOverlay.Count(o => o.Op == "contour")}");
            var drills = _opsOverlay.Count(o => o.Op == "drill");
            if (drills > 0)
                RoutesOpsList.Items.Add($"钻孔 × {drills}");
        }
    }

    // ----- 原料管理 -----
    void RefreshMaterialsModule()
    {
        MaterialsList.Items.Clear();
        MaterialsList.Items.Add("— 车间材料库 —");
        foreach (var m in _library.Materials)
            MaterialsList.Items.Add($"[库] {m.Id} · {m.Name} · t={m.ThicknessMm:0.#} · {m.DensityHint ?? ""}");
        if (_session.Package is null)
        {
            MaterialsMeta.Text = $"库材料 {_library.Materials.Count} · 尚未载入方案";
            return;
        }
        MaterialsList.Items.Add("— 当前方案板材 —");
        foreach (var s in _session.Package.Sheets)
            MaterialsList.Items.Add($"[方案] {s.SheetId} · {s.Material ?? "—"} · {s.WidthMm:0.#}x{s.LengthMm:0.#} · t={s.ThicknessMm:0.#}");
        var mats = _session.Package.Panels.Select(p => p.Material).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x);
        MaterialsList.Items.Add("— 方案材料用量 —");
        foreach (var name in mats)
            MaterialsList.Items.Add($"[用] {name} · panels={_session.Package.Panels.Count(p => p.Material == name)}");
        MaterialsMeta.Text =
            $"库 {_library.Materials.Count} · 方案 sheets={_session.Package.Sheets.Count} panels={_session.Package.Panels.Count}";
    }

    void OnMaterialAddClick(object sender, RoutedEventArgs e)
    {
        var name = (MatNameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        var id = "mat_" + name.Replace(' ', '_').ToLowerInvariant();
        var existing = _library.Materials.FindIndex(m => m.Id == id || m.Name == name);
        var row = new LibMaterial
        {
            Id = id,
            Name = name,
            ThicknessMm = ParseMm(MatThickBox.Text, 18),
            DensityHint = string.IsNullOrWhiteSpace(MatHintBox.Text) ? null : MatHintBox.Text.Trim(),
        };
        if (existing >= 0) _library.Materials[existing] = row;
        else _library.Materials.Add(row);
        PersistLibrary();
        RefreshMaterialsModule();
    }

    void OnMaterialDeleteClick(object sender, RoutedEventArgs e)
    {
        // only delete library rows: map selected text back
        if (MaterialsList.SelectedItem is not string s || !s.StartsWith("[库] ")) return;
        var id = s["[库] ".Length..].Split('·')[0].Trim();
        _library.Materials.RemoveAll(m => m.Id == id);
        PersistLibrary();
        RefreshMaterialsModule();
    }

    void OnMaterialSyncPackageClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("无方案可同步");
            return;
        }
        var added = SyncMaterialsFromPackage(_session.Package, _session.LastImportSnapshot);
        PersistLibrary();
        RefreshMaterialsModule();
        SetStatus(added > 0 ? $"已从方案同步 {added} 种材料到车间库" : "车间库材料已是最新");
    }

    int SyncMaterialsFromPackage(CutPackage pkg, ManufacturingSnapshot? snapshot)
    {
        var added = 0;
        if (snapshot?.Materials is { Count: > 0 })
        {
            foreach (var mat in snapshot.Materials)
            {
                if (string.IsNullOrWhiteSpace(mat.MaterialId)) continue;
                if (_library.Materials.Any(m => m.Id == mat.MaterialId || m.Name == mat.MaterialId
                        || (!string.IsNullOrWhiteSpace(mat.DisplayName) && m.Name == mat.DisplayName)))
                    continue;
                var thickness = mat.ThicknessMm is > 0
                    ? mat.ThicknessMm.Value
                    : pkg.Panels.FirstOrDefault(p => p.Material == mat.MaterialId)?.ThicknessMm ?? 18;
                _library.Materials.Add(new LibMaterial
                {
                    Id = mat.MaterialId,
                    Name = string.IsNullOrWhiteSpace(mat.DisplayName) ? mat.MaterialId : mat.DisplayName!,
                    ThicknessMm = thickness,
                    DensityHint = mat.SubstrateId,
                });
                added++;
            }
            return added;
        }

        foreach (var name in pkg.Panels.Select(p => p.Material).Where(m => !string.IsNullOrEmpty(m)).Distinct())
        {
            var id = "mat_" + name!.Replace(' ', '_').ToLowerInvariant();
            if (_library.Materials.Any(m => m.Id == id || m.Name == name)) continue;
            var t = pkg.Panels.First(p => p.Material == name).ThicknessMm;
            _library.Materials.Add(new LibMaterial { Id = id, Name = name, ThicknessMm = t > 0 ? t : 18 });
            added++;
        }
        return added;
    }

    // ----- 工艺模版 -----
    void RefreshProcessModule()
    {
        ProcessToolsList.Items.Clear();
        foreach (var t in _library.Tools)
            ProcessToolsList.Items.Add($"{t.Id} · {t.Name} · Ø{t.DiameterMm:0.#} · F{t.FeedXyMmMin:0}/Z{t.FeedZMmMin:0} · {t.SpindleRpm:0}rpm");
        var activeIndex = _library.Tools.FindIndex(t => t.Id == _activeToolId);
        if (activeIndex >= 0) ProcessToolsList.SelectedIndex = activeIndex;
        RebuildOpsOverlay();
        ProcessOpsList.Items.Clear();
        if (_opsOverlay.Count == 0)
            ProcessOpsList.Items.Add("无作业工序");
        else
            foreach (var g in new[]
                     {
                         TroyPassKind.TongueGroove,
                         TroyPassKind.Clearance,
                         TroyPassKind.ProfileFirst,
                         TroyPassKind.Drilling,
                     })
            {
                var n = _opsOverlay.Count(o => TroyPass.InPass(o, g));
                if (n > 0)
                    ProcessOpsList.Items.Add($"{TroyPass.Title(g)} × {n}");
            }
        var activeTool = _library.Tools.FirstOrDefault(t => t.Id == _activeToolId);
        ProcessMeta.Text =
            $"刀具 {_library.Tools.Count} · 作业工序 {_opsOverlay.Count} · 机型 {SelectedMachineId()} · " +
            $"当前刀具 {(activeTool?.Name ?? "机型默认")}";
    }

    void OnToolAddClick(object sender, RoutedEventArgs e)
    {
        var name = (ToolNameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        var id = "tool_" + name.Replace(' ', '_').ToLowerInvariant();
        var row = new LibTool
        {
            Id = id,
            Name = name,
            MachineId = SelectedMachineId(),
            DiameterMm = ParseMm(ToolDiaBox.Text, 6),
            FeedXyMmMin = ParseMm(ToolFeedXyBox.Text, 3000),
            FeedZMmMin = 500,
            SpindleRpm = ParseMm(ToolRpmBox.Text, 18000),
        };
        var i = _library.Tools.FindIndex(t => t.Id == id);
        if (i >= 0) _library.Tools[i] = row;
        else _library.Tools.Add(row);
        PersistLibrary();
        RefreshProcessModule();
    }

    void OnProcessToolSelected(object sender, SelectionChangedEventArgs e)
    {
        var i = ProcessToolsList.SelectedIndex;
        if (i < 0 || i >= _library.Tools.Count) return;
        var t = _library.Tools[i];
        ToolNameBox.Text = t.Name;
        ToolDiaBox.Text = t.DiameterMm.ToString("0.###");
        ToolFeedXyBox.Text = t.FeedXyMmMin.ToString("0.###");
        ToolRpmBox.Text = t.SpindleRpm.ToString("0.###");
    }

    void OnToolApplyClick(object sender, RoutedEventArgs e)
    {
        var i = ProcessToolsList.SelectedIndex;
        if (i < 0 || i >= _library.Tools.Count)
        {
            SetStatus("请先选择刀具");
            return;
        }
        var tool = _library.Tools[i];
        _activeToolId = tool.Id;
        RebuildOpsOverlay();
        RegenerateNcFromCurrentOps();
        RefreshProcessModule();
        CanvasHost.InvalidateVisual();
        SetStatus($"已应用刀具 · {tool.Name} Ø{tool.DiameterMm:0.###} · F{tool.FeedXyMmMin:0}");
    }

    void OnToolDeleteClick(object sender, RoutedEventArgs e)
    {
        var i = ProcessToolsList.SelectedIndex;
        if (i < 0 || i >= _library.Tools.Count) return;
        if (_library.Tools[i].Id == _activeToolId) _activeToolId = null;
        _library.Tools.RemoveAt(i);
        PersistLibrary();
        RefreshProcessModule();
    }

    void OnToolResetFromMachinesClick(object sender, RoutedEventArgs e)
    {
        _library.Tools = WorkshopLibraryStore.CreateDefault().Tools;
        PersistLibrary();
        RefreshProcessModule();
    }

    // ----- 参数设置 -----
    void ApplyLibraryToSettingsUi()
    {
        SetSheetWBox.Text = _library.Nest.DefaultSheetWidthMm.ToString("0.###");
        SetSheetLBox.Text = _library.Nest.DefaultSheetLengthMm.ToString("0.###");
        SetSpacingBox.Text = _library.Nest.SpacingMm.ToString("0.###");
        SetBorderBox.Text = _library.Nest.BorderMm.ToString("0.###");
        SetAllowRotChk.IsChecked = _library.Nest.AllowRotation;
        SetLabelDirBox.Text = _library.Labeler.MachinePictureDir;
    }

    void ApplyLibraryToNestBoxes()
    {
        StockWidthBox.Text = _library.Nest.DefaultSheetWidthMm.ToString("0.###");
        StockLengthBox.Text = _library.Nest.DefaultSheetLengthMm.ToString("0.###");
        NestSpacingBox.Text = _library.Nest.SpacingMm.ToString("0.###");
        NestBorderBox.Text = _library.Nest.BorderMm.ToString("0.###");
        NestAllowRotChk.IsChecked = _library.Nest.AllowRotation;
    }

    void RefreshSettingsModule()
    {
        ApplyLibraryToSettingsUi();
        SettingsMeta.Text =
            $"库路径: {WorkshopLibraryStore.DefaultPath()}\n" +
            $"savedAt: {_library.SavedAt ?? "—"}\n" +
            $"materials={_library.Materials.Count} tools={_library.Tools.Count} remnants={_library.Remnants.Count}";
    }

    void ReadSettingsUiIntoLibrary()
    {
        _library.Nest.DefaultSheetWidthMm = ParseMm(SetSheetWBox.Text, 1200);
        _library.Nest.DefaultSheetLengthMm = ParseMm(SetSheetLBox.Text, 2400);
        _library.Nest.SpacingMm = ParseMm(SetSpacingBox.Text, 12);
        _library.Nest.BorderMm = ParseMm(SetBorderBox.Text, 15);
        _library.Nest.AllowRotation = SetAllowRotChk.IsChecked == true;
        var labelDir = SetLabelDirBox.Text.Trim();
        _library.Labeler.MachinePictureDir = labelDir.Length > 0 ? labelDir : new LabelerDefaults().MachinePictureDir;
    }

    void OnSettingsSaveClick(object sender, RoutedEventArgs e)
    {
        ReadSettingsUiIntoLibrary();
        PersistLibrary();
        RefreshSettingsModule();
        SetStatus($"参数已保存 · 机床标签目录 {_library.Labeler.MachinePictureDir}", StatusKind.Success);
    }

    void OnSettingsApplyClick(object sender, RoutedEventArgs e)
    {
        ReadSettingsUiIntoLibrary();
        PersistLibrary();
        ApplyLibraryToNestBoxes();
        SetStatus("参数已应用到生产加工排版框");
    }

    void OnSettingsResetClick(object sender, RoutedEventArgs e)
    {
        _library.Nest = new NestDefaults();
        PersistLibrary();
        RefreshSettingsModule();
    }


    void RebuildOpsOverlay()
    {
        _opsOverlay = [];
        if (_session.Package is null || _nest is not { Ok: true })
        {
            RefreshOpsRail();
            RefreshCamFrames();
            RefreshPreflightMeta();
            RegenerateNcFromCurrentOps();
            return;
        }
        var places = _nest.Placements.Select(p => new NestPlacement
        {
            PanelId = p.PanelId,
            SheetIndex = p.SheetIndex,
            OffsetX = p.OffsetX,
            OffsetY = p.OffsetY,
            RotationDeg = p.RotationDeg,
        }).Where(p => _opsAllSheets || p.SheetIndex == _activeNestSheet).ToList();
        ReadClearanceFields();
        ReadDrillFields();
        var raw = OpsPlanner.FeaturesToOps(
            _session.Package.Panels,
            enableContour: true,
            enableDrill: true,
            enableGroove: true,
            clearanceLargeMinShortMm: _clrLargeMinShort,
            drillMaxExclusiveMm: _drillMaxExclusive);
        _opsOverlay = OpsPlanner.AttachToNest(raw, places);
        _opsOverlay = ApplyAutomaticToolOffset(_opsOverlay);
        _opsOverlay = _opsOverlay
            .Where(PassEnabled)
            .ToList();
        _opsOverlay = _opsOverlay.Concat(BuildGuillotineOps()).ToList();
        var kept = ProfileBridgePlanner.Reproject(_profileBridges, _opsOverlay);
        _profileBridges.Clear();
        _profileBridges.AddRange(kept);
        RefreshBridgeCount();
        RefreshOpsRail();
        RefreshCamFrames();
        RefreshPreflightMeta();
        RegenerateNcFromCurrentOps();
    }

    bool PassEnabled(CutOp op)
    {
        var kind = op.Op ?? "";
        if (kind.Equals("drill", StringComparison.OrdinalIgnoreCase))
            return _enableDrilling;
        if (kind.Equals("groove", StringComparison.OrdinalIgnoreCase) && op.IsTongue)
            return _enableTongue;
        if (kind.Equals("pocket", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("groove", StringComparison.OrdinalIgnoreCase))
            return _enableClearance;
        if (kind.Equals("contour", StringComparison.OrdinalIgnoreCase))
            return _enableProfile || _enableProfileLast;
        if (kind.Equals(GuillotineCutPlanner.OpKind, StringComparison.OrdinalIgnoreCase))
            return true;
        return true;
    }

    IReadOnlyList<CutOp> BuildGuillotineOps()
    {
        var ops = new List<CutOp>();
        foreach (var (sheet, plan) in _guillotineBySheet)
        {
            if (!_opsAllSheets && sheet != _activeNestSheet) continue;
            var (sw, sh, th) = SheetCamMetrics(sheet);
            ops.AddRange(GuillotineCutPlanner.ToCutOps(plan, sheet, sw, sh, th, toolDiameterMm: 10));
        }
        return ops;
    }

    (double Width, double Length, double Thickness) SheetCamMetrics(int sheet)
    {
        if (sheet >= 0 && sheet < _nestSheetsUsed.Count)
        {
            var s = _nestSheetsUsed[sheet];
            var th = s.ThicknessMm > 0
                ? s.ThicknessMm
                : _session.Package?.Panels.FirstOrDefault()?.ThicknessMm ?? 18;
            return (s.WidthMm, s.LengthMm, th > 0 ? th : 18);
        }
        var pkgSheet = _session.Package?.Sheets.FirstOrDefault();
        var w = ParseMm(StockWidthBox.Text, pkgSheet?.WidthMm > 0 ? pkgSheet.WidthMm : 1200);
        var h = ParseMm(StockLengthBox.Text, pkgSheet?.LengthMm > 0 ? pkgSheet.LengthMm : 2400);
        var fallbackTh = _session.Package?.Panels.FirstOrDefault()?.ThicknessMm ?? 18;
        return (w, h, fallbackTh > 0 ? fallbackTh : 18);
    }

    IReadOnlyDictionary<int, double> ShopToolDiaByNum()
    {
        var map = new Dictionary<int, double>();
        foreach (var t in _library.Tools)
        {
            if (t.DiameterMm <= 0 || string.IsNullOrWhiteSpace(t.Id)) continue;
            var id = t.Id.Trim();
            if (id.Length >= 2 && (id[0] is 'T' or 't')
                && int.TryParse(id.AsSpan(1), out var n) && n > 0)
                map[n] = t.DiameterMm;
        }
        return map;
    }

    double ToolDiameterOf(CutOp op)
    {
        if (!string.IsNullOrWhiteSpace(op.ToolId))
        {
            var lib = _library.Tools.FirstOrDefault(t =>
                t.Id.Equals(op.ToolId, StringComparison.OrdinalIgnoreCase));
            if (lib is { DiameterMm: > 0 }) return lib.DiameterMm;
            if (ToolCatalog.DefaultMap().TryGetValue(op.ToolId, out var preset) && preset.DiameterMm > 0)
                return preset.DiameterMm;
        }
        return ActiveProfileForCam().ToolDiameterMm is > 0 and var d ? d : 6.35;
    }

    IReadOnlyList<CutOp> ApplyAutomaticToolOffset(IReadOnlyList<CutOp> ops)
    {
        var contour = ops.FirstOrDefault(o => o.Op == "contour");
        var radius = ToolDiameterOf(contour ?? new CutOp { Op = "contour", PanelId = "_" }) / 2;
        if (radius < 1e-6) return ops;
        return ContourToolOffset.Apply(ops, radius);
    }

    void RefreshOpsRail()
    {
        SyncStrategyCheckboxes();
        var nProfile = _opsOverlay.Count(o => CamStrategy.Classify(o) == CamStrategyKind.Profile);
        var nClear = _opsOverlay.Count(o => CamStrategy.Classify(o) == CamStrategyKind.AreaClearance);
        var nDrill = _opsOverlay.Count(o => CamStrategy.Classify(o) == CamStrategyKind.Drilling);
        var nGuill = _opsOverlay.Count(o => CamStrategy.Classify(o) == CamStrategyKind.Guillotine);
        OpsIconProfileCount.Text = nProfile > 0 ? $"{nProfile}" : "";
        OpsIconClearanceCount.Text = nClear > 0 ? $"{nClear}" : "";
        OpsIconDrillCount.Text = nDrill > 0 ? $"{nDrill}" : "";
        RefreshGuillotineBox();
        if (nGuill > 0)
            OpsIconGuillotineCount.Text = $"{nGuill}";
        _opsSummary = _nest is not { Ok: true }
            ? "请先完成密排"
            : _opsOverlay.Count == 0
                ? "无刀路 — 点右下计算当前板材或全部"
                : $"Profiling {nProfile} · Area Clearance {nClear} · Drilling {nDrill}";
        RefreshDrillSummary();
    }

    void RefreshDrillSummary()
    {
        var drills = _opsOverlay.Where(o => o.Op == "drill").ToList();
        if (drills.Count == 0)
        {
            DrillSummary.Text = "";
            return;
        }
        var groups = drills
            .GroupBy(o => o.DiameterMm ?? 0)
            .OrderBy(g => g.Key)
            .Select(g => $"Ø{g.Key:0.##} × {g.Count()}");
        DrillSummary.Text = string.Join(" · ", groups);
    }

    sealed record OpsToolChoice(string Id, string Label);

    void BindOpsToolCombos()
    {
        var tools = new List<OpsToolChoice>();
        foreach (var t in ToolCatalog.DefaultPresets)
            tools.Add(new OpsToolChoice(t.ToolId, $"{t.ToolId}  Ø{t.DiameterMm:0.##}  {t.Name}"));
        foreach (var t in _library.Tools)
        {
            if (tools.Any(x => x.Id.Equals(t.Id, StringComparison.OrdinalIgnoreCase))) continue;
            tools.Add(new OpsToolChoice(t.Id, $"{t.Id}  Ø{t.DiameterMm:0.##}  {t.Name}"));
        }
        _syncingOpsStrategy = true;
        ProfFirstTool.ItemsSource = tools;
        ProfFirstTool.DisplayMemberPath = nameof(OpsToolChoice.Label);
        ProfFirstTool.SelectedValuePath = nameof(OpsToolChoice.Id);
        ProfLastTool.ItemsSource = tools;
        ProfLastTool.DisplayMemberPath = nameof(OpsToolChoice.Label);
        ProfLastTool.SelectedValuePath = nameof(OpsToolChoice.Id);
        ProfFirstTool.SelectedValue = "T2";
        ProfLastTool.SelectedValue = "T2";
        _syncingOpsStrategy = false;
    }

    void OnProfileToolChanged(object sender, SelectionChangedEventArgs e)
    {
        ReadProfileFields();
        RegenerateNcFromCurrentOps();
    }

    void OnProfileFieldChanged(object sender, RoutedEventArgs e)
    {
        ReadProfileFields();
        RegenerateNcFromCurrentOps();
    }

    void OnClearanceFieldChanged(object sender, RoutedEventArgs e)
    {
        ReadClearanceFields();
        RegenerateNcFromCurrentOps();
    }

    void OnDrillFieldChanged(object sender, RoutedEventArgs e)
    {
        ReadDrillFields();
        RegenerateNcFromCurrentOps();
    }

    void OnOpsHomeXyClick(object sender, RoutedEventArgs e)
    {
        _homeXyAtEnd = OpsHomeXyChk.IsChecked == true;
        RegenerateNcFromCurrentOps();
    }

    void OnDrillThresholdChanged(object sender, RoutedEventArgs e)
    {
        ReadDrillFields();
        if (_opsOverlay.Count == 0) return;
        RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
    }

    void ReadDrillFields()
    {
        if (_syncingOpsStrategy) return;
        _drillPlunge = ParseMm(DrillPlunge.Text, TroyRecipe.PlungeFeedMmMin);
        _drillRpm = ParseMm(DrillRpm.Text, TroyRecipe.SpindleRpm);
        _drillThrough = ParseSigned(DrillThrough.Text, TroyRecipe.ThroughZMm);
        _drillMaxExclusive = ClearanceToolPick.NormalizeDrillMaxExclusiveMm(
            ParseMm(DrillMaxExclusive.Text, ClearanceToolPick.DrillMaxExclusiveMm));
    }

    void OnGuillotineFieldChanged(object sender, RoutedEventArgs e) => ReadGuillotineFields();

    void ReadGuillotineFields()
    {
        if (_syncingOpsStrategy) return;
        _guillotineFeed = ParseMm(GuillotineFeed.Text, TroyRecipe.GuillotineFeedMmMin);
        _guillotinePlunge = ParseMm(GuillotinePlunge.Text, TroyRecipe.GuillotinePlungeMmMin);
        _guillotineThrough = ParseSigned(GuillotineThrough.Text, TroyRecipe.GuillotineThroughZMm);
    }

    void RefreshGuillotineBox()
    {
        var n = _guillotineBySheet.Values.Sum(p => p.Cuts.Count);
        var show = n > 0;
        OpsIconGuillotine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OpsIconGuillotineCount.Text = show ? $"{n}" : "";
        if (!show && _opsStrategy is CamStrategyKind.Guillotine)
        {
            _opsStrategy = null;
            ApplyOpsChrome();
        }
    }

    IReadOnlyList<ProfileBridge> FacingBridgesForExport()
    {
        var paired = ProfileBridgePlanner.EnsureFacingPairs(
            _profileBridges,
            _opsOverlay,
            AllSheetOutlines(),
            LastProfileToolDiameterMm());
        var before = _profileBridges.Count;
        var beforePaired = _profileBridges.Count(b => b.PairId is not null);
        var afterPaired = paired.Count(b => b.PairId is not null);
        if (paired.Count != before || afterPaired != beforePaired)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(paired);
            RefreshBridgeCount();
        }

        return paired;
    }

    PostRecipe CurrentPostRecipe()
    {
        ReadProfileFields();
        ReadClearanceFields();
        ReadDrillFields();
        ReadGuillotineFields();
        _homeXyAtEnd = OpsHomeXyChk.IsChecked == true;
        return new PostRecipe
        {
            SafeZMm = TroyRecipe.SafeZMm,
            Z0IsBoardBottom = true,
            TongueFeed = _tongueFeed,
            TongueRpm = _tongueRpm,
            TonguePlunge = _tonguePlunge,
            ClearanceFeed = _clrFeed,
            ClearanceRpm = _clrRpm,
            ClearancePlunge = _clrPlunge,
            ProfileFirstFeed = _profFirstFeed,
            ProfileFirstRpm = _profFirstRpm,
            ProfileFirstPlunge = _profFirstPlunge,
            ProfileFirstRamp45 = _profFirstRamp45,
            ProfileFirstLeaveMm = _profFirstLeave,
            ProfileLastFeed = _profLastFeed,
            ProfileLastRpm = _profLastRpm,
            ProfileLastPlunge = _profLastPlunge,
            ProfileThroughZMm = _profLastThrough,
            DrillPlunge = _drillPlunge,
            DrillRpm = _drillRpm,
            DrillThroughZMm = _drillThrough,
            GuillotineFeed = _guillotineFeed,
            GuillotinePlunge = _guillotinePlunge,
            GuillotineThroughZMm = _guillotineThrough,
            HomeXyAtEnd = _homeXyAtEnd,
            Bridges = _enableBridges ? FacingBridgesForExport() : [],
        };
    }

    void OnClearanceThresholdChanged(object sender, RoutedEventArgs e)
    {
        ReadClearanceFields();
        if (_opsOverlay.Count == 0) return;
        RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
    }

    void ReadClearanceFields()
    {
        if (_syncingOpsStrategy) return;
        _clrFeed = ParseMm(ClrFeed.Text, TroyRecipe.WorkFirstFeedMmMin);
        _clrRpm = ParseMm(ClrRpm.Text, TroyRecipe.SpindleRpm);
        _clrPlunge = ParseMm(ClrPlunge.Text, TroyRecipe.PlungeFeedMmMin);
        _clrLargeMinShort = ClearanceToolPick.NormalizeLargeMinShortMm(
            ParseMm(ClrLargeMinShort.Text, ClearanceToolPick.LargeMinShortMm));
    }

    void ReadProfileFields()
    {
        if (_syncingOpsStrategy) return;
        _profFirstTool = ProfFirstTool.SelectedValue as string ?? "T2";
        _profLastTool = ProfLastTool.SelectedValue as string ?? "T2";
        _profFirstFeed = ParseMm(ProfFirstFeed.Text, 12000);
        _profFirstRpm = ParseMm(ProfFirstRpm.Text, 14500);
        _profFirstPlunge = ParseMm(ProfFirstPlunge.Text, 1000);
        _profFirstRamp45 = ProfFirstRamp45.IsChecked == true;
        _profFirstLeave = ParseMm(ProfFirstLeave.Text, 0.5);
        _profLastFeed = ParseMm(ProfLastFeed.Text, 20000);
        _profLastRpm = ParseMm(ProfLastRpm.Text, 14500);
        _profLastPlunge = ParseMm(ProfLastPlunge.Text, 1000);
        _profLastThrough = ParseSigned(ProfLastThrough.Text, -0.55);
        _tongueFeed = ParseMm(TongueFeed.Text, TroyRecipe.TongueFeedMmMin);
        _tongueRpm = ParseMm(TongueRpm.Text, TroyRecipe.SpindleRpm);
        _tonguePlunge = ParseMm(TonguePlunge.Text, TroyRecipe.PlungeFeedMmMin);
        _homeXyAtEnd = OpsHomeXyChk.IsChecked == true;
        _profBridgeWidth = ParseMm(ProfBridgeWidth.Text, ProfileBridgePlanner.DefaultWidthMm);
        if (_profBridgeWidth > 80) _profBridgeWidth = 80;
        _profTinyAreaM2 = ParseMm(ProfBridgeTinyArea.Text, ProfileBridgePlanner.TinyAreaM2);
        _profLargeAreaM2 = ParseMm(ProfBridgeLargeArea.Text, ProfileBridgePlanner.LargeAreaM2);
        (_profTinyAreaM2, _profLargeAreaM2) = ProfileBridgePlanner.NormalizeAreaLimits(
            _profTinyAreaM2, _profLargeAreaM2);
        _profStripAspect = ProfileBridgePlanner.NormalizeStripAspect(
            ParseMm(ProfBridgeStripAspect.Text, ProfileBridgePlanner.StripAspect));
        foreach (var i in Enumerable.Range(0, _profileBridges.Count).ToList())
            _profileBridges[i] = _profileBridges[i] with { WidthMm = _profBridgeWidth };
    }

    IReadOnlyList<CutOp> FocusedOps() =>
        _opsOverlay.Where(o => _opsStrategy is null || CamStrategy.Classify(o) == _opsStrategy).ToList();

    const double OpsIconRailW = 88;
    const double OpsParamsW = 300;

    void ApplyOpsChrome()
    {
        var open = _opsStrategy is not null;
        OpsParamsPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NcCol.Width = new GridLength(open ? OpsIconRailW + OpsParamsW : OpsIconRailW);
        OpsParamsProfile.Visibility = _opsStrategy is CamStrategyKind.Profile ? Visibility.Visible : Visibility.Collapsed;
        OpsParamsClearance.Visibility = _opsStrategy is CamStrategyKind.AreaClearance ? Visibility.Visible : Visibility.Collapsed;
        OpsParamsDrill.Visibility = _opsStrategy is CamStrategyKind.Drilling ? Visibility.Visible : Visibility.Collapsed;
        OpsParamsGuillotine.Visibility = _opsStrategy is CamStrategyKind.Guillotine ? Visibility.Visible : Visibility.Collapsed;
        OpsParamsTitle.Text = _opsStrategy switch
        {
            CamStrategyKind.Profile => "Profiling",
            CamStrategyKind.AreaClearance => "Area Clearance",
            CamStrategyKind.Drilling => "Drilling",
            CamStrategyKind.Guillotine => "Guillotine cut",
            _ => "刀路参数",
        };
        _opsFocus = _opsStrategy switch
        {
            CamStrategyKind.Profile => TroyPassKind.ProfileFirst,
            CamStrategyKind.AreaClearance => TroyPassKind.Clearance,
            CamStrategyKind.Drilling => TroyPassKind.Drilling,
            _ => null,
        };
        _syncingOpsIcons = true;
        OpsIconProfile.IsChecked = _opsStrategy is CamStrategyKind.Profile;
        OpsIconClearance.IsChecked = _opsStrategy is CamStrategyKind.AreaClearance;
        OpsIconDrill.IsChecked = _opsStrategy is CamStrategyKind.Drilling;
        OpsIconGuillotine.IsChecked = _opsStrategy is CamStrategyKind.Guillotine;
        _syncingOpsIcons = false;
    }

    void OnOpsToolpathIconClick(object sender, RoutedEventArgs e)
    {
        if (_syncingOpsIcons) return;
        if (sender is not ToggleButton btn || btn.Tag is not string tag
            || !Enum.TryParse<CamStrategyKind>(tag, out var kind))
            return;
        _opsStrategy = btn.IsChecked == true ? kind : null;
        ApplyOpsChrome();
        RefreshOpsRail();
        RefreshCamFrames();
        CanvasHost.InvalidateVisual();
    }

    void SyncStrategyCheckboxes()
    {
        _syncingOpsStrategy = true;
        OpsTongueChk.IsChecked = _enableTongue;
        OpsProfileChk.IsChecked = _enableProfile;
        OpsProfileLastChk.IsChecked = _enableProfileLast;
        OpsBridgeChk.IsChecked = _enableBridges;
        OpsClearanceChk.IsChecked = _enableClearance;
        OpsDrillChk.IsChecked = _enableDrilling;
        RouteTongueChk.IsChecked = _enableTongue;
        RouteContourChk.IsChecked = _enableProfile && _enableProfileLast;
        RouteGrooveChk.IsChecked = _enableClearance;
        RouteDrillChk.IsChecked = _enableDrilling;
        _syncingOpsStrategy = false;
    }

    void OnOpsMachineChanged(object sender, SelectionChangedEventArgs e) =>
        SyncMachineSelection(OpsMachineCombo.SelectedValue as string);

    void OnOpsCalculateCurrentClick(object sender, RoutedEventArgs e) =>
        CalculateOps(allSheets: false);

    void OnOpsCalculateAllClick(object sender, RoutedEventArgs e) =>
        CalculateOps(allSheets: true);

    void OnProfBridgeManualClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "ops")
        {
            SetStatus("请到刀路页使用手动布桥");
            return;
        }
        _bridgeManualMode = !_bridgeManualMode;
        if (_bridgeManualMode)
        {
            _bridgeDeleteMode = false;
            _opsStrategy = CamStrategyKind.Profile;
            ApplyOpsChrome();
            CanvasHost.InvalidateVisual();
        }
        ApplyBridgeModeChrome();
        SetStatus(_bridgeManualMode
            ? "手动模式中 · 点外形刀路轨迹放桥"
            : "已退出手动模式");
    }

    void OnProfBridgeAutoClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "ops")
        {
            SetStatus("请到刀路页使用自动布桥");
            return;
        }
        if (!_opsOverlay.Any(o => o.Op == "contour" && o.Placed))
        {
            SetStatus("请先计算刀路");
            return;
        }
        ReadProfileFields();
        var result = ProfileBridgePlanner.AutoPlace(
            _profileBridges,
            _opsOverlay,
            CurrentSheetOutlines(),
            _activeNestSheet,
            LastProfileToolDiameterMm(),
            _profBridgeWidth,
            _profTinyAreaM2,
            _profLargeAreaM2,
            _profStripAspect);
        if (result.Changed)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(result.Bridges);
            RefreshBridgeCount();
            RegenerateNcFromCurrentOps();
            CanvasHost.InvalidateVisual();
        }
        SetStatus(result.Message);
    }

    void OnProfBridgeAutoAllClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "ops")
        {
            SetStatus("请到刀路页使用批量自动布桥");
            return;
        }
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("请先完成密排");
            return;
        }
        ReadProfileFields();
        var nestSheets = _nest.Placements.Select(p => p.SheetIndex).Distinct().ToHashSet();
        var opSheets = _opsOverlay
            .Where(o => o.Op == "contour" && o.Placed)
            .Select(o => o.SheetIndex)
            .ToHashSet();
        if (nestSheets.Count == 0 || !nestSheets.IsSubsetOf(opSheets))
            CalculateOps(allSheets: true);
        if (!_opsOverlay.Any(o => o.Op == "contour" && o.Placed))
        {
            SetStatus("请先计算刀路");
            return;
        }
        var result = ProfileBridgePlanner.AutoPlaceAll(
            _profileBridges,
            _opsOverlay,
            AllSheetOutlines(),
            LastProfileToolDiameterMm(),
            _profBridgeWidth,
            _profTinyAreaM2,
            _profLargeAreaM2,
            _profStripAspect);
        if (result.Changed)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(result.Bridges);
            RefreshBridgeCount();
            RegenerateNcFromCurrentOps();
            CanvasHost.InvalidateVisual();
        }
        SetStatus(result.Message);
    }

    void OnProfBridgeClearClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "ops")
        {
            SetStatus("请到刀路页清空桥");
            return;
        }
        var result = ProfileBridgePlanner.ClearSheet(_profileBridges, _activeNestSheet);
        if (result.Changed)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(result.Bridges);
            RefreshBridgeCount();
            RegenerateNcFromCurrentOps();
            CanvasHost.InvalidateVisual();
        }
        SetStatus(result.Message);
    }

    void OnProfBridgeDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "ops")
        {
            SetStatus("请到刀路页删除桥");
            return;
        }
        _bridgeDeleteMode = !_bridgeDeleteMode;
        if (_bridgeDeleteMode)
        {
            _bridgeManualMode = false;
            _opsStrategy = CamStrategyKind.Profile;
            ApplyOpsChrome();
            CanvasHost.InvalidateVisual();
        }
        ApplyBridgeModeChrome();
        SetStatus(_bridgeDeleteMode
            ? "删除中 · 点桥标记去掉，成对一起删"
            : "已退出删除");
    }

    void ApplyBridgeModeChrome()
    {
        if (_bridgeManualMode)
        {
            ProfBridgeManualBtn.Content = "手动模式中";
            ProfBridgeManualBtn.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x5C, 0x26));
            ProfBridgeManualBtn.Foreground = Brushes.White;
            ProfBridgeManualBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x3B, 0x12));
        }
        else
        {
            ProfBridgeManualBtn.Content = "手动模式";
            ProfBridgeManualBtn.Background = Brushes.White;
            ProfBridgeManualBtn.Foreground = Brushes.Black;
            ProfBridgeManualBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        }

        if (_bridgeDeleteMode)
        {
            ProfBridgeDeleteBtn.Content = "删除中";
            ProfBridgeDeleteBtn.Background = new SolidColorBrush(Color.FromRgb(0xB0, 0x3A, 0x2E));
            ProfBridgeDeleteBtn.Foreground = Brushes.White;
            ProfBridgeDeleteBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7B, 0x24, 0x1C));
        }
        else
        {
            ProfBridgeDeleteBtn.Content = "删除桥";
            ProfBridgeDeleteBtn.Background = Brushes.White;
            ProfBridgeDeleteBtn.Foreground = Brushes.Black;
            ProfBridgeDeleteBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        }
    }

    void ApplyBridgeManualChrome() => ApplyBridgeModeChrome();

    void ExitBridgeModes()
    {
        if (!_bridgeManualMode && !_bridgeDeleteMode) return;
        _bridgeManualMode = false;
        _bridgeDeleteMode = false;
        ApplyBridgeModeChrome();
    }

    void ExitBridgeManualMode() => ExitBridgeModes();

    void ResetProfileBridges()
    {
        _profileBridges.Clear();
        RefreshBridgeCount();
    }

    void RefreshBridgeCount()
    {
        var n = _profileBridges.Count;
        var paired = _profileBridges.Count(b => b.PairId is not null);
        ProfBridgeCount.Text = n == 0
            ? ""
            : $"已放 {n} 个" + (paired > 0 ? $"（成对 {paired}）" : "");
    }

    double LastProfileToolDiameterMm()
    {
        var op = new CutOp { Op = "contour", PanelId = "_", ToolId = _profLastTool };
        var d = ToolDiameterOf(op);
        return d > 0 ? d : TroyRecipe.WorkDiameterMm;
    }

    Dictionary<string, IReadOnlyList<Point2>> CurrentSheetOutlines() =>
        SheetOutlines(_activeNestSheet);

    Dictionary<string, IReadOnlyList<Point2>> AllSheetOutlines() =>
        SheetOutlines(null);

    Dictionary<string, IReadOnlyList<Point2>> SheetOutlines(int? sheetIndex)
    {
        var map = new Dictionary<string, IReadOnlyList<Point2>>(StringComparer.Ordinal);
        if (_nest is not { Ok: true } || _session.Package is null) return map;
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        foreach (var place in _nest.Placements.Where(p => sheetIndex is null || p.SheetIndex == sheetIndex))
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            if (panel.Outline.Points.Count < 2) continue;
            map[place.PanelId] = NestTransform.SheetOutline(
                panel, place.OffsetX, place.OffsetY, place.RotationDeg);
        }
        return map;
    }

    bool TryHandleBridgeManualClick(float sx, float sy)
    {
        if (!_opsOverlay.Any(o => o.Op == "contour" && o.Placed))
        {
            SetStatus("请先计算刀路");
            return true;
        }
        EnsureNestViewMetrics();
        var (mx, my) = ScreenToSheet(sx, sy);
        var scale = Math.Max(0.01, _nestScale);
        var hitTol = Math.Max(8, 14.0 / scale);
        var symbolTol = Math.Max(3.5, 9.0 / scale);
        ReadProfileFields();
        var result = ProfileBridgePlanner.HandleClick(
            _profileBridges,
            _opsOverlay,
            CurrentSheetOutlines(),
            _activeNestSheet,
            mx,
            my,
            LastProfileToolDiameterMm(),
            _profBridgeWidth,
            hitTol,
            symbolTol);
        if (result.Changed)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(result.Bridges);
            RefreshBridgeCount();
            RegenerateNcFromCurrentOps();
            CanvasHost.InvalidateVisual();
        }
        SetStatus(result.Message + (_profileBridges.Count > 0 ? $" · 共 {_profileBridges.Count} 个" : ""));
        return true;
    }

    bool TryHandleBridgeDeleteClick(float sx, float sy)
    {
        EnsureNestViewMetrics();
        var (mx, my) = ScreenToSheet(sx, sy);
        var scale = Math.Max(0.01, _nestScale);
        var symbolTol = Math.Max(3.5, 9.0 / scale);
        var result = ProfileBridgePlanner.HandleDelete(
            _profileBridges, _activeNestSheet, mx, my, symbolTol);
        if (result.Changed)
        {
            _profileBridges.Clear();
            _profileBridges.AddRange(result.Bridges);
            RefreshBridgeCount();
            RegenerateNcFromCurrentOps();
            CanvasHost.InvalidateVisual();
        }
        SetStatus(result.Message + (_profileBridges.Count > 0 ? $" · 剩 {_profileBridges.Count} 个" : ""));
        return true;
    }

    void CalculateOps(bool allSheets)
    {
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("刀路：请先完成密排");
            return;
        }
        _opsAllSheets = allSheets;
        RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
        SetStatus(allSheets
            ? $"已计算全部大板 · {_opsSummary}"
            : $"已计算当前大板 {_activeNestSheet + 1} · {_opsSummary}");
    }

    void OnOpsCalculateClick(object sender, RoutedEventArgs e) =>
        CalculateOps(allSheets: false);

    void RefreshCamFrames()
    {
        var ops = FocusedOps();
        _camFrames = CamSimulator.ExpandFrames(ops);
        _camFrameIndex = _camFrames.Count == 0
            ? 0
            : Math.Clamp(_camFrameIndex, 0, _camFrames.Count - 1);
    }

    void StepCam(int delta)
    {
        if (_camFrames.Count == 0)
        {
            _camTimer.Stop();
            return;
        }
        _camFrameIndex = CamSimulator.Step(_camFrameIndex, _camFrames.Count, delta);
        CanvasHost.InvalidateVisual();
    }

    void OnNestStabilizeClick(object sender, RoutedEventArgs e)
    {
        var (sw, sh, _) = ActiveSheetMetrics();
        var borderMm = ActiveSheetBorderMm();
        var spacingMm = ActiveSheetSpacingMm();
        UsageLog.LogEvent("ui", "desktop.nestStabilize.click", new Dictionary<string, object?>
        {
            ["sheetIndex"] = _activeNestSheet,
            ["sheetLabel"] = $"大板 {_activeNestSheet + 1}",
            ["sheetW"] = sw,
            ["sheetH"] = sh,
            ["borderMm"] = borderMm,
            ["spacingMm"] = spacingMm,
            ["hasPackage"] = _session.Package is not null,
            ["nestOk"] = _nest is { Ok: true },
            ["placementCount"] = _nest?.Placements.Count ?? 0,
            ["lockedCount"] = _locked.Count,
            ["partInPartSlots"] = _partInPartSlots.Count,
        });

        if (_session.Package is null || _nest is not { Ok: true })
        {
            const string skip = "密排优化：请先完成密排";
            UsageLog.LogEvent("ui", "desktop.nestStabilize.result", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["improved"] = false,
                ["message"] = skip,
                ["why"] = "no-nest",
            });
            SetStatus(skip);
            return;
        }

        var frozen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in _partInPartSlots)
        {
            frozen.Add(slot.HostPanelId);
            frozen.Add(slot.ChildPanelId);
        }

        var result = SheetStabilityOptimizer.Optimize(
            _session.Package.Panels,
            CurrentNestPlacements(),
            _activeNestSheet,
            sw,
            sh,
            borderMm,
            spacingMm,
            _locked,
            frozen,
            _partInPartSlots);

        UsageLog.LogEvent("ui", "desktop.nestStabilize.result", new Dictionary<string, object?>
        {
            ["ok"] = result.Improved,
            ["improved"] = result.Improved,
            ["movedCount"] = result.MovedCount,
            ["stripMoved"] = result.StripMoved,
            ["largeMoved"] = result.LargeMoved,
            ["pipMoved"] = result.PipMoved,
            ["stripCount"] = result.StripCount,
            ["columnCount"] = result.ColumnCount,
            ["startScore"] = result.StartScore,
            ["endScore"] = result.EndScore,
            ["message"] = result.Message,
            ["reasons"] = result.Reasons.ToList(),
            ["sheetIndex"] = _activeNestSheet,
        });

        if (!result.Improved)
        {
            SetStatus($"大板 {_activeNestSheet + 1}: {result.Message}");
            return;
        }

        var byId = result.Placements.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
        foreach (var place in _nest.Placements)
        {
            if (!byId.TryGetValue(place.PanelId, out var next)) continue;
            place.SheetIndex = next.SheetIndex;
            place.OffsetX = next.OffsetX;
            place.OffsetY = next.OffsetY;
            place.RotationDeg = next.RotationDeg;
        }

        _guillotineBySheet.Remove(_activeNestSheet);
        if (_opsOverlay.Count > 0)
            RebuildOpsOverlay();
        RefreshNestReport();
        CanvasHost.InvalidateVisual();
        SetStatus($"大板 {_activeNestSheet + 1}: {result.Message}");
    }

    (double Clearance, double MinEdge) ReadGuillotineGeometry()
    {
        var clearance = GuillotineClearanceBox is not null
            ? ParseMm(GuillotineClearanceBox.Text, GuillotineCutPlanner.DefaultClearanceMm)
            : GuillotineCutPlanner.DefaultClearanceMm;
        var minEdge = GuillotineMinEdgeBox is not null
            ? ParseMm(GuillotineMinEdgeBox.Text, GuillotineCutPlanner.MinRemnantEdgeMm)
            : GuillotineCutPlanner.MinRemnantEdgeMm;
        if (clearance < 0) clearance = 0;
        if (minEdge < 1) minEdge = GuillotineCutPlanner.MinRemnantEdgeMm;
        return (clearance, minEdge);
    }

    void OnNestGuillotineClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("余料线：请先完成密排");
            return;
        }

        // Toggle off if already computed for this sheet.
        if (_guillotineBySheet.ContainsKey(_activeNestSheet))
        {
            _guillotineBySheet.Remove(_activeNestSheet);
            SetStatus($"大板 {_activeNestSheet + 1}: 已清除余料线");
            if (_opsOverlay.Count > 0)
                RebuildOpsOverlay();
            RefreshGuillotineBox();
            CanvasHost.InvalidateVisual();
            return;
        }

        var (sw, sh, _) = ActiveSheetMetrics();
        var (clearance, minEdge) = ReadGuillotineGeometry();
        var plan = GuillotineCutPlanner.PlanSheet(
            _session.Package.Panels,
            CurrentNestPlacements(),
            _activeNestSheet,
            sw,
            sh,
            clearance,
            minEdge);
        if (plan is null)
        {
            SetStatus($"大板 {_activeNestSheet + 1}: 无合法余料线（余料边 < {minEdge:0}mm 或已铺满）");
            MessageBox.Show(this,
                $"当前大板没有可用的余料。\n\n规则：距排版外包 {clearance:0}mm；\n" +
                $"优先方料，拆开后任一边 < {minEdge:0}mm 则改留 L。",
                "本张余料线",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _guillotineBySheet[_activeNestSheet] = plan;
        SetStatus($"大板 {_activeNestSheet + 1}: {plan.Label}");
        if (_opsOverlay.Count > 0)
            RebuildOpsOverlay();
        RefreshGuillotineBox();
        CanvasHost.InvalidateVisual();
    }

    void OnNestGuillotineAllClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("余料线：请先完成密排");
            return;
        }

        var (clearance, minEdge) = ReadGuillotineGeometry();
        var places = CurrentNestPlacements();
        var total = NestSheetCount();
        var ok = 0;
        var skip = 0;
        _guillotineBySheet.Clear();
        for (var i = 0; i < total; i++)
        {
            var (sw, sh, th) = SheetCamMetrics(i);
            _ = th;
            var plan = GuillotineCutPlanner.PlanSheet(
                _session.Package.Panels, places, i, sw, sh, clearance, minEdge);
            if (plan is null)
            {
                skip++;
                continue;
            }
            _guillotineBySheet[i] = plan;
            ok++;
        }

        SetStatus(ok == 0
            ? $"全部余料线：{total} 张大板均无合法余料（间隙 {clearance:0} · 最短边 {minEdge:0}）"
            : $"全部余料线：{ok} 张已生成" + (skip > 0 ? $" · {skip} 张跳过" : ""));
        if (_opsOverlay.Count > 0)
            RebuildOpsOverlay();
        RefreshGuillotineBox();
        CanvasHost.InvalidateVisual();
    }

    void OnNestVerifyPolyClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("多边形校验：无排版");
            return;
        }
        var places = CurrentNestPlacements();
        var hits = NestValidator.FindPolygonCollisions(
            _session.Package.Panels,
            places,
            ParseMm(NestSpacingBox.Text, 12),
            PipIgnorePairs());
        var msg = hits.Count == 0
            ? "Clipper2 多边形 + 间距校验通过"
            : $"发现 {hits.Count} 处多边形/间距冲突：\n" +
              string.Join("\n", hits.Take(20).Select(h => $"{h.PanelIdA} × {h.PanelIdB} · S{h.SheetIndex + 1}"));
        SetStatus(msg.Replace("\n", " · "));
        MessageBox.Show(this, msg, hits.Count == 0 ? "排版校验通过" : "排版校验失败",
            MessageBoxButton.OK, hits.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        CanvasHost.InvalidateVisual();
    }

    List<NestPlacement> CurrentNestPlacements() =>
        _nest?.Placements.Select(p => new NestPlacement
        {
            PanelId = p.PanelId,
            SheetIndex = p.SheetIndex,
            OffsetX = p.OffsetX,
            OffsetY = p.OffsetY,
            RotationDeg = p.RotationDeg,
        }).ToList() ?? [];

    IReadOnlyDictionary<string, (double X, double Y)>? CurrentLabelOverrides() =>
        _labelOverrides.Count == 0 ? null : _labelOverrides;

    LabelAnchor ResolveLabelAnchor(PanelPart panel, double rotationDeg) =>
        _labelOverrides.TryGetValue(panel.PanelId, out var ov)
            ? LabelAnchorFinder.Find(panel, rotationDeg, ov)
            : LabelAnchorFinder.Find(panel, rotationDeg);

    void RefreshPreflightMeta()
    {
        if (_opsOverlay.Count == 0)
        {
            PreflightMeta.Text = "";
            return;
        }
        var report = RunPreflight();
        var text = NcPreflight.Format(report);
        PreflightMeta.Text = text;
        PreflightMeta.Foreground = report.Ok
            ? new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0x88))
            : new SolidColorBrush(Color.FromRgb(0xE0, 0x88, 0x88));
    }

    PreflightReport RunPreflight(IReadOnlyList<ExportNcFile>? files = null, bool allSheets = false)
    {
        var profile = ActiveProfileForCam();
        var panels = _session.Package?.Panels.ToDictionary(p => p.PanelId, p => p);
        return NcPreflight.Check(
            OpsForPreflight(files, allSheets),
            profile,
            ParseMm(StockWidthBox.Text, 1200),
            ParseMm(StockLengthBox.Text, 2400),
            panels);
    }

    IReadOnlyList<CutOp> OpsForPreflight(IReadOnlyList<ExportNcFile>? files, bool allSheets)
    {
        if (allSheets) return _opsOverlay;
        files ??= ExportFilesOfSelectedKind();
        if (files.Count == 0) return _opsOverlay;
        var sheets = files.Select(f => f.SheetIndex).ToHashSet();
        return _opsOverlay.Where(o => sheets.Contains(o.SheetIndex)).ToList();
    }

    HashSet<string>? _conflictCache;

    HashSet<string> CurrentConflicts()
    {
        if (_session.Package is null || _nest is not { Ok: true }) return [];
        if (_dragMode is "nest" or "label" or "nestBox")
            return _conflictCache ?? [];
        var places = _nest.Placements
            .Where(p => p.SheetIndex == _activeNestSheet)
            .Select(p => new NestPlacement
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                RotationDeg = p.RotationDeg,
            }).ToList();
        var hits = NestValidator.FindPolygonCollisions(
            _session.Package.Panels,
            places,
            ActiveSheetSpacingMm(),
            PipIgnorePairs());
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hits)
        {
            set.Add(h.PanelIdA);
            set.Add(h.PanelIdB);
        }
        _conflictCache = set;
        return set;
    }

    HashSet<(string A, string B)>? PipIgnorePairs() =>
        _partInPartSlots.Count == 0
            ? null
            : PartsInPartPacker.IgnoreCollisionPairs(_partInPartSlots);

    (double Ox, double Oy) ClampPipChild(
        string childId,
        PanelPart child,
        double ox,
        double oy,
        double rotDeg)
    {
        var slot = _partInPartSlots.FirstOrDefault(s =>
            s.Enabled && string.Equals(s.ChildPanelId, childId, StringComparison.Ordinal));
        if (slot is null || _session.Package is null || _nest is not { Ok: true })
            return (ox, oy);
        var hostPlace = _nest.Placements.FirstOrDefault(p => p.PanelId == slot.HostPanelId);
        var host = _session.Package.Panels.FirstOrDefault(p => p.PanelId == slot.HostPanelId);
        if (hostPlace is null || host is null) return (ox, oy);
        var gap = ParseMm(NestSpacingBox.Text, 12);
        if (!PartsInPartPacker.TryUsableVoid(
                host, hostPlace.OffsetX, hostPlace.OffsetY, hostPlace.RotationDeg,
                slot.FeatureId, gap, out var vx, out var vy, out var vw, out var vh))
            return (ox, oy);
        return NestDrag.ClampInBounds(child, ox, oy, rotDeg, vx, vy, vx + vw, vy + vh);
    }

    void SyncNestSettingsFromPackage()
    {
        if (_session.Package is null) return;
        var sheet = _session.Package.Sheets.FirstOrDefault();
        if (sheet is not null)
        {
            if (sheet.WidthMm > 0) StockWidthBox.Text = sheet.WidthMm.ToString("0.###");
            if (sheet.LengthMm > 0) StockLengthBox.Text = sheet.LengthMm.ToString("0.###");
            if (sheet.MarginMm > 0) NestBorderBox.Text = sheet.MarginMm.ToString("0.###");
            var gap = sheet.PartClearanceMm > 0 ? sheet.PartClearanceMm : sheet.KerfMm;
            if (gap > 0) NestSpacingBox.Text = gap.ToString("0.###");
        }
    }

    void RefreshNestReport(bool full = true)
    {
        NestUnplacedList.Items.Clear();
        NestGroupReportList.Items.Clear();
        RefreshNestStockSummary();
        if (_nest is not { Ok: true })
        {
            NestReportMeta.Text = "尚未排版 — 请回板材页点「初始密排」，或点下方「重新密排」";
            NestWarningsExpander.Header = "未排 / 警告";
            NestWarningsExpander.IsExpanded = false;
            UpdateNestSheetChrome();
            return;
        }

        double used = 0;
        if (_session.Package is not null)
        {
            var placed = _nest.Placements.Select(p => p.PanelId).ToHashSet();
            foreach (var p in _session.Package.Panels.Where(p => placed.Contains(p.PanelId)))
            {
                var (w, h) = SizeOf(p);
                used += w * h;
            }
        }
        var sheets = Math.Max(1, NestSheetCount());
        double sheetArea = 0;
        if (_nestSheetsUsed.Count > 0)
            sheetArea = _nestSheetsUsed.Sum(s => s.WidthMm * s.LengthMm);
        else
        {
            var (sw, sh, _) = ActiveSheetMetrics();
            sheetArea = sw * sh * sheets;
        }
        var util = sheetArea > 0 ? used / sheetArea * 100 : 0;
        var gateOk = true;
        if (full && _session.Package is not null)
        {
            var spacing = _stockKinds.Count > 0
                ? _stockKinds.Min(k => k.SpacingMm)
                : ParseMm(NestSpacingBox.Text, 12);
            var gate = NestExportGate.Check(
                _session.Package.Panels,
                CurrentNestPlacements(),
                spacing,
                allowAabbOverlap: UsesTrueShapeNest(),
                partInPartSlots: _partInPartSlots);
            gateOk = gate.Ok;
        }
        var engineLabel = _nest.Engine switch
        {
            var e when e.StartsWith("clipper_nfp", StringComparison.OrdinalIgnoreCase) => "推荐精排",
            var e when e.StartsWith("deepnest", StringComparison.OrdinalIgnoreCase) => "实验预览",
            var e when e.Contains("blf", StringComparison.OrdinalIgnoreCase) => "快速矩形",
            _ => _nest.Engine,
        };
        NestReportMeta.Text =
            (_session.ManufacturingDirty
                ? "材料已改 · 摆位仍是旧的 · 改完后点「重新密排」\n"
                : "") +
            $"利用率 {util:0.0}%\n" +
            $"大板 {sheets} 张 · 已排 {_nest.Placements.Count} · 待用 {_nestHolding.Count} · 未排 {_nest.Unplaced.Count}\n" +
            $"面积 {used / 1e6:0.000} / {sheetArea / 1e6:0.000} m²\n" +
            $"方式 {engineLabel}" +
            (gateOk ? " · 校验通过" : " · 校验有问题（见警告）");

        if (_session.Package is not null)
        {
            var byGroup = _session.Package.Panels
                .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
                .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Key.ThicknessMm);
            var placedIds = _nest.Placements.Select(p => p.PanelId).ToHashSet();
            var sheetByPanel = _nest.Placements.ToDictionary(p => p.PanelId, p => p.SheetIndex);
            foreach (var g in byGroup)
            {
                var ids = g.Select(p => p.PanelId).ToList();
                var placedCount = ids.Count(id => placedIds.Contains(id));
                var sheetIdx = ids.Where(id => sheetByPanel.ContainsKey(id)).Select(id => sheetByPanel[id]).Distinct().Count();
                var sample = g.First();
                NestGroupReportList.Items.Add(
                    $"{KindDisplayName(sample)} · {placedCount}/{ids.Count} 件 · {sheetIdx} 张板");
            }
        }

        var warnCount = _nest.Unplaced.Count + _nest.Warnings.Count;
        NestWarningsExpander.Header = warnCount == 0 ? "未排 / 警告（无）" : $"未排 / 警告（{warnCount}）";
        NestWarningsExpander.IsExpanded = warnCount > 0 && (_nest.Unplaced.Count > 0 || !gateOk);
        if (warnCount == 0)
            NestUnplacedList.Items.Add("全部已排 · 无警告");
        else
        {
            foreach (var id in _nest.Unplaced)
                NestUnplacedList.Items.Add($"未排 {id}");
            foreach (var w in _nest.Warnings.Take(30))
                NestUnplacedList.Items.Add($"{w.Message}");
        }
        UpdateNestSheetChrome();
    }


    static double ParseMm(string? text, double fallback) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : fallback;

    static double ParseSigned(string? text, double fallback) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    static string CamBox(double v) =>
        v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    static string LeftoverBox(double v) =>
        v > 0 ? CamBox(v) : "";

    void ClearManufacturingState()
    {
        _nest = null;
        _nestSheetsUsed = [];
        _partInPartSlots = [];
        _guillotineBySheet.Clear();
        _nestHolding.Clear();
        _holdingLayout = [];
        _holdingRegions = [];
        _activeNestSheet = 0;
        _showNest = false;
        _opsOverlay = [];
        _exportFiles = [];
        _exportSelected = null;
        _opsAllSheets = true;
        NcPreview.Text = "";
        ResetProfileBridges();
        ExitBridgeModes();
        _locked.Clear();
        _labelOverrides.Clear();
    }

    ProjectSessionState CaptureProjectSession()
    {
        ReadProfileFields();
        ReadClearanceFields();
        ReadDrillFields();
        _homeXyAtEnd = OpsHomeXyChk.IsChecked == true;
        return new ProjectSessionState
        {
            Stage = _stage,
            LabelerMachineId = SelectedLabelerMachineId(),
            ActiveNestSheet = _activeNestSheet,
            OpsAllSheets = _opsAllSheets,
            ShowNest = _showNest,
            NestEngine = _nest?.Engine,
            NestEnginePreference = (NestEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string,
            NestSheetCount = _nest?.SheetCount ?? _nestSheetsUsed.Count,
            SelectedExportFile = _exportSelected?.FileName,
            Unplaced = _nest?.Unplaced.ToList() ?? [],
            LockedPanelIds = _locked.ToList(),
            NestSheetsUsed = _nestSheetsUsed.Select(ProjectSessionCodec.FromSheet).ToList(),
            StockKinds = _stockKinds.Select(k => new StockKindDto
            {
                MaterialId = k.MaterialId,
                Label = k.Label,
                ThicknessMm = k.ThicknessMm,
                WidthMm = k.WidthMm,
                LengthMm = k.LengthMm,
                SpacingMm = k.SpacingMm,
                BorderMm = k.BorderMm,
                AllowRotate90 = k.AllowRotate90,
                SheetGrainKey = k.SheetGrainKey,
                AllowPartsInPart = k.AllowPartsInPart,
                UseLeftoverPieces = k.UseLeftoverPieces,
                LeftoverXMm = k.LeftoverXMm,
                LeftoverYMm = k.LeftoverYMm,
            }).ToList(),
            Holding = _nestHolding.Select(h => new HeldPartDto
            {
                PanelId = h.PanelId,
                Material = h.Material,
                ThicknessMm = h.ThicknessMm,
                RotationDeg = h.RotationDeg,
                WidthMm = h.WidthMm,
                HeightMm = h.HeightMm,
            }).ToList(),
            PartInPart = _partInPartSlots.Select(s => new PartInPartDto
            {
                HostPanelId = s.HostPanelId,
                ChildPanelId = s.ChildPanelId,
                FeatureId = s.FeatureId,
                SheetIndex = s.SheetIndex,
                Enabled = s.Enabled,
            }).ToList(),
            Guillotine = _guillotineBySheet.Select(kv => new GuillotineDto
            {
                SheetIndex = kv.Key,
                Kind = kv.Value.Kind,
                Label = kv.Value.Label,
                RemnantAreaMm2 = kv.Value.RemnantAreaMm2,
                RemnantMinEdgeMm = kv.Value.RemnantMinEdgeMm,
                Polyline = kv.Value.Polyline.Select(p => new XyDto { X = p.X, Y = p.Y }).ToList(),
                Cuts = kv.Value.Cuts.Select(c => new GuillotineCutDto
                {
                    Kind = c.Kind,
                    Label = c.Label,
                    RemnantAreaMm2 = c.RemnantAreaMm2,
                    RemnantMinEdgeMm = c.RemnantMinEdgeMm,
                    Polyline = c.Polyline.Select(p => new XyDto { X = p.X, Y = p.Y }).ToList(),
                }).ToList(),
                Pieces = kv.Value.Pieces.Select(p => new GuillotinePieceDto
                {
                    Shape = p.Shape,
                    W = p.W,
                    H = p.H,
                    AreaMm2 = p.AreaMm2,
                    MinEdgeMm = p.MinEdgeMm,
                    LabelX = p.LabelX,
                    LabelY = p.LabelY,
                    Label = p.Label,
                }).ToList(),
            }).ToList(),
            Cam = new ProjectCamSettings
            {
                EnableTongue = _enableTongue,
                EnableProfile = _enableProfile,
                EnableProfileLast = _enableProfileLast,
                EnableClearance = _enableClearance,
                EnableBridges = _enableBridges,
                EnableDrilling = _enableDrilling,
                HomeXyAtEnd = _homeXyAtEnd,
                ProfFirstTool = _profFirstTool,
                ProfLastTool = _profLastTool,
                ProfFirstFeed = _profFirstFeed,
                ProfFirstRpm = _profFirstRpm,
                ProfFirstPlunge = _profFirstPlunge,
                ProfFirstRamp45 = _profFirstRamp45,
                ProfFirstLeave = _profFirstLeave,
                ProfLastFeed = _profLastFeed,
                ProfLastRpm = _profLastRpm,
                ProfLastPlunge = _profLastPlunge,
                ProfLastThrough = _profLastThrough,
                TongueFeed = _tongueFeed,
                TongueRpm = _tongueRpm,
                TonguePlunge = _tonguePlunge,
                ProfBridgeWidth = _profBridgeWidth,
                ProfTinyAreaM2 = _profTinyAreaM2,
                ProfLargeAreaM2 = _profLargeAreaM2,
                ProfStripAspect = _profStripAspect,
                ClrFeed = _clrFeed,
                ClrRpm = _clrRpm,
                ClrPlunge = _clrPlunge,
                ClrLargeMinShort = _clrLargeMinShort,
                DrillPlunge = _drillPlunge,
                DrillRpm = _drillRpm,
                DrillThrough = _drillThrough,
                DrillMaxExclusive = _drillMaxExclusive,
                GuillotineFeed = _guillotineFeed,
                GuillotinePlunge = _guillotinePlunge,
                GuillotineThrough = _guillotineThrough,
            },
            Bridges = _profileBridges.Select(ProjectSessionCodec.FromBridge).ToList(),
            Ops = _opsOverlay.Select(ProjectSessionCodec.FromOp).ToList(),
            LabelAnchors = _labelOverrides.Select(kv => new LabelAnchorDto
            {
                PanelId = kv.Key,
                LocalX = kv.Value.X,
                LocalY = kv.Value.Y,
            }).ToList(),
        };
    }

    void ApplyProjectSession(ProjectSessionState state)
    {
        ApplyLabelerSelection(state.LabelerMachineId);
        ApplyCamSettings(state.Cam);
        if (!string.IsNullOrWhiteSpace(state.NestEnginePreference))
        {
            foreach (var item in NestEngineCombo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, state.NestEnginePreference, StringComparison.OrdinalIgnoreCase))
                {
                    NestEngineCombo.SelectedItem = item;
                    break;
                }
            }
        }

        _stockKinds.Clear();
        foreach (var k in state.StockKinds)
        {
            _stockKinds.Add(new StockMaterialKindVm
            {
                MaterialId = k.MaterialId,
                AutoLabel = KindAutoLabel(k.MaterialId, k.ThicknessMm) ?? k.Label,
                Label = string.IsNullOrWhiteSpace(k.Label) ? k.MaterialId : k.Label,
                ThicknessMm = k.ThicknessMm,
                PanelCount = 0,
                WidthMmText = CamBox(k.WidthMm),
                LengthMmText = CamBox(k.LengthMm),
                SpacingMmText = CamBox(k.SpacingMm),
                BorderMmText = CamBox(k.BorderMm),
                AllowRotate90 = k.AllowRotate90,
                SheetGrainKey = string.IsNullOrWhiteSpace(k.SheetGrainKey) ? "none" : k.SheetGrainKey,
                AllowPartsInPart = k.AllowPartsInPart,
                UseLeftoverPieces = k.UseLeftoverPieces,
                LeftoverXMmText = LeftoverBox(k.LeftoverXMm),
                LeftoverYMmText = LeftoverBox(k.LeftoverYMm),
            });
        }

        _opsAllSheets = state.OpsAllSheets;
        _activeNestSheet = Math.Max(0, state.ActiveNestSheet);
        _nestSheetsUsed = state.NestSheetsUsed.Select(ProjectSessionCodec.ToSheet).ToList();
        _partInPartSlots = state.PartInPart.Select(s => new PartInPartSlot
        {
            HostPanelId = s.HostPanelId,
            ChildPanelId = s.ChildPanelId,
            FeatureId = s.FeatureId,
            SheetIndex = s.SheetIndex,
            Enabled = s.Enabled,
        }).ToList();
        _guillotineBySheet.Clear();
        foreach (var g in state.Guillotine)
        {
            var cuts = g.Cuts.Count > 0
                ? g.Cuts.Select(c => new GuillotineCutPlanner.Result
                {
                    Kind = string.IsNullOrWhiteSpace(c.Kind) ? g.Kind : c.Kind,
                    Label = c.Label,
                    RemnantAreaMm2 = c.RemnantAreaMm2,
                    RemnantMinEdgeMm = c.RemnantMinEdgeMm,
                    Polyline = c.Polyline.Select(p => (p.X, p.Y)).ToList(),
                }).ToList()
                : g.Polyline.Count >= 2
                    ? [new GuillotineCutPlanner.Result
                    {
                        Kind = g.Kind,
                        Label = g.Label,
                        RemnantAreaMm2 = g.RemnantAreaMm2,
                        RemnantMinEdgeMm = g.RemnantMinEdgeMm,
                        Polyline = g.Polyline.Select(p => (p.X, p.Y)).ToList(),
                    }]
                    : [];
            if (cuts.Count == 0) continue;
            _guillotineBySheet[g.SheetIndex] = new GuillotineCutPlanner.SheetPlan
            {
                Cuts = cuts,
                Pieces = g.Pieces.Select(p => new GuillotineCutPlanner.RemnantPiece
                {
                    Shape = string.IsNullOrWhiteSpace(p.Shape) ? "RECT" : p.Shape,
                    W = p.W,
                    H = p.H,
                    AreaMm2 = p.AreaMm2,
                    MinEdgeMm = p.MinEdgeMm,
                    LabelX = p.LabelX,
                    LabelY = p.LabelY,
                    Label = p.Label,
                }).ToList(),
                Label = g.Label,
                RemnantAreaMm2 = g.RemnantAreaMm2,
                RemnantMinEdgeMm = g.RemnantMinEdgeMm,
            };
        }
        _nestHolding.Clear();
        foreach (var h in state.Holding)
        {
            _nestHolding.Add(new HeldNestPart
            {
                PanelId = h.PanelId,
                Material = h.Material,
                ThicknessMm = h.ThicknessMm,
                RotationDeg = h.RotationDeg,
                WidthMm = h.WidthMm,
                HeightMm = h.HeightMm,
            });
        }
        _locked.Clear();
        foreach (var id in state.LockedPanelIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            _locked.Add(id);

        _profileBridges.Clear();
        _profileBridges.AddRange(state.Bridges.Select(ProjectSessionCodec.ToBridge));
        _opsOverlay = state.Ops.Select(ProjectSessionCodec.ToOp).ToList();
        _labelOverrides.Clear();
        foreach (var a in state.LabelAnchors)
        {
            if (string.IsNullOrWhiteSpace(a.PanelId)) continue;
            _labelOverrides[a.PanelId] = (a.LocalX, a.LocalY);
        }
    }

    void ApplyCamSettings(ProjectCamSettings cam)
    {
        _enableTongue = cam.EnableTongue;
        _enableProfile = cam.EnableProfile;
        _enableProfileLast = cam.EnableProfileLast;
        _enableClearance = cam.EnableClearance;
        _enableBridges = cam.EnableBridges;
        _enableDrilling = cam.EnableDrilling;
        _homeXyAtEnd = cam.HomeXyAtEnd;
        _profFirstTool = string.IsNullOrWhiteSpace(cam.ProfFirstTool) ? "T2" : cam.ProfFirstTool;
        _profLastTool = string.IsNullOrWhiteSpace(cam.ProfLastTool) ? "T2" : cam.ProfLastTool;
        _profFirstFeed = cam.ProfFirstFeed;
        _profFirstRpm = cam.ProfFirstRpm;
        _profFirstPlunge = cam.ProfFirstPlunge;
        _profFirstRamp45 = cam.ProfFirstRamp45;
        _profFirstLeave = cam.ProfFirstLeave;
        _profLastFeed = cam.ProfLastFeed;
        _profLastRpm = cam.ProfLastRpm;
        _profLastPlunge = cam.ProfLastPlunge;
        _profLastThrough = cam.ProfLastThrough;
        _tongueFeed = cam.TongueFeed;
        _tongueRpm = cam.TongueRpm;
        _tonguePlunge = cam.TonguePlunge;
        _profBridgeWidth = cam.ProfBridgeWidth;
        _profTinyAreaM2 = cam.ProfTinyAreaM2;
        _profLargeAreaM2 = cam.ProfLargeAreaM2;
        _profStripAspect = cam.ProfStripAspect;
        _clrFeed = cam.ClrFeed;
        _clrRpm = cam.ClrRpm;
        _clrPlunge = cam.ClrPlunge;
        _clrLargeMinShort = cam.ClrLargeMinShort;
        _drillPlunge = cam.DrillPlunge;
        _drillRpm = cam.DrillRpm;
        _drillThrough = cam.DrillThrough;
        _drillMaxExclusive = cam.DrillMaxExclusive;
        _guillotineFeed = cam.GuillotineFeed;
        _guillotinePlunge = cam.GuillotinePlunge;
        _guillotineThrough = cam.GuillotineThrough;
        WriteCamToUi();
    }

    void WriteCamToUi()
    {
        _syncingOpsStrategy = true;
        TongueFeed.Text = CamBox(_tongueFeed);
        TongueRpm.Text = CamBox(_tongueRpm);
        TonguePlunge.Text = CamBox(_tonguePlunge);
        ProfFirstTool.SelectedValue = _profFirstTool;
        ProfLastTool.SelectedValue = _profLastTool;
        ProfFirstFeed.Text = CamBox(_profFirstFeed);
        ProfFirstRpm.Text = CamBox(_profFirstRpm);
        ProfFirstPlunge.Text = CamBox(_profFirstPlunge);
        ProfFirstRamp45.IsChecked = _profFirstRamp45;
        ProfFirstLeave.Text = CamBox(_profFirstLeave);
        ProfLastFeed.Text = CamBox(_profLastFeed);
        ProfLastRpm.Text = CamBox(_profLastRpm);
        ProfLastPlunge.Text = CamBox(_profLastPlunge);
        ProfLastThrough.Text = CamBox(_profLastThrough);
        ProfBridgeWidth.Text = CamBox(_profBridgeWidth);
        ProfBridgeTinyArea.Text = CamBox(_profTinyAreaM2);
        ProfBridgeLargeArea.Text = CamBox(_profLargeAreaM2);
        ProfBridgeStripAspect.Text = CamBox(_profStripAspect);
        ClrFeed.Text = CamBox(_clrFeed);
        ClrRpm.Text = CamBox(_clrRpm);
        ClrPlunge.Text = CamBox(_clrPlunge);
        ClrLargeMinShort.Text = CamBox(_clrLargeMinShort);
        DrillPlunge.Text = CamBox(_drillPlunge);
        DrillRpm.Text = CamBox(_drillRpm);
        DrillThrough.Text = CamBox(_drillThrough);
        DrillMaxExclusive.Text = CamBox(_drillMaxExclusive);
        GuillotineFeed.Text = CamBox(_guillotineFeed);
        GuillotinePlunge.Text = CamBox(_guillotinePlunge);
        GuillotineThrough.Text = CamBox(_guillotineThrough);
        OpsHomeXyChk.IsChecked = _homeXyAtEnd;
        _syncingOpsStrategy = false;
        SyncStrategyCheckboxes();
    }

    void RestoreProjectStage(string? requested)
    {
        var stage = requested is "stock" or "nest" or "ops" or "out" or "load" ? requested : "load";
        if (stage is "ops" or "out" && _opsOverlay.Count == 0)
            stage = _nest is { Ok: true } ? "nest" : "stock";
        if (stage is "nest" && _nest is not { Ok: true })
            stage = "stock";
        if (_session.Package is null)
            stage = "load";
        _stageChanging = true;
        _stage = stage;
        StageTabs.SelectedIndex = stage switch
        {
            "stock" => 1,
            "nest" => 2,
            "ops" => 3,
            "out" => 4,
            _ => 0,
        };
        _stageChanging = false;
        _showNest = (_stage is "nest" or "ops" or "out") && _nest is { Ok: true };
    }

    void TryLoadDemoPackage() => OnLoadDemoClick(this, new RoutedEventArgs());

    void OnLoadDemoClick(object sender, RoutedEventArgs e)
    {
        var demo = FindDemoPackage();
        if (demo is null)
        {
            SetStatus("示例包未找到 · public/samples/demo_manufacturing_snapshot.json");
            ShowImportDialog(false, "打开示例", "demo_manufacturing_snapshot.json", null, "示例包未找到（public/samples）");
            return;
        }
        var result = _session.OpenPackageFile(demo);
        if (!result.Ok)
        {
            SetStatus("示例方案载入失败: " + string.Join("; ", result.Errors.Select(err => err.Message)), StatusKind.Error);
            ShowImportDialog(false, "打开示例", Path.GetFileName(demo), result);
            return;
        }
        ClearManufacturingState();
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        _stageChanging = true;
        StageTabs.SelectedIndex = 0;
        _stage = "load";
        _stageChanging = false;
        ApplyStageVisibility();
        UpdateStageChrome();
        UpdateCanvasHint();
        RefreshWorkflowDots();
        MarkWorkSaved();
        SetStatus($"已载入示例 · {_session.Package!.Panels.Count} 块板 · 警告 {result.Warnings.Count}", StatusKind.Success);
        ShowImportDialog(true, "打开示例", Path.GetFileName(demo), result);
    }

    static string? FindDemoPackage()
    {
        var walk = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            foreach (var rel in new[]
                     {
                         Path.Combine("public", "samples", "demo_manufacturing_snapshot.json"),
                         Path.Combine("public", "samples", "demo_snapshot_single_side.cnjob"),
                         Path.Combine("public", "samples", "demo_cut_package.json"),
                         Path.Combine("public", "samples", "demo_woodjob_120.zip"),
                     })
            {
                var p = Path.Combine(walk, rel);
                if (File.Exists(p)) return p;
                var alt = Path.GetFullPath(Path.Combine(walk, "..", "..", "..", "..", "..", rel));
                if (File.Exists(alt)) return alt;
            }
            var parent = Directory.GetParent(walk);
            if (parent is null) break;
            walk = parent.FullName;
        }
        return null;
    }

    void BindPackage()
    {
        WarnList.Items.Clear();
        if (_session.Package is null)
        {
            BindPartList(null);
            PackageMeta.Text = "尚未加载";
            SyncProjectNameBox();
            RefreshEmptyState();
            ApplyStageVisibility();
            RefreshWorkflowDots();
            return;
        }
        BindPartList(_session.Package.Panels.FirstOrDefault()?.PanelId);
        // Seed workshop materials from snapshot catalog when present (Fusion .cnjob).
        if (SyncMaterialsFromPackage(_session.Package, _session.LastImportSnapshot) > 0)
            PersistLibrary();
        var pkgCount = _session.Package.Panels
            .Select(p => p.DisplayPackage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        PackageMeta.Text =
            $"{_session.Package.SchemaName} v{_session.Package.Version}\n" +
            $"工程={_session.ResolvedProjectName} · packages={pkgCount} · panels={_session.Package.Panels.Count} · sheets={_session.Package.Sheets.Count}";
        SyncProjectNameBox();
        foreach (var w in _session.LastWarnings.Take(20))
            WarnList.Items.Add($"{w.Code}: {w.Message}");
        SyncNestSettingsFromPackage();
        RefreshStockMaterialCards();
        RefreshNestReport();
        RefreshGeomRail();
        RefreshWorkflowDots();
        RefreshEmptyState();
        ApplyStageVisibility();
        RefreshMaterialsModule();
        CanvasHost.InvalidateVisual();
    }

    bool _stockGrainUiQuiet;

    void RefreshStockMaterialCards()
    {
        _stockGrainUiQuiet = true;
        var defaultsW = ParseMm(StockWidthBox.Text, _library.Nest.DefaultSheetWidthMm);
        var defaultsL = ParseMm(StockLengthBox.Text, _library.Nest.DefaultSheetLengthMm);
        var defaultsSpacing = ParseMm(NestSpacingBox.Text, _library.Nest.SpacingMm > 0 ? _library.Nest.SpacingMm : 12);
        var defaultsBorder = ParseMm(NestBorderBox.Text, _library.Nest.BorderMm > 0 ? _library.Nest.BorderMm : 15);
        var defaultsAllowRot = NestAllowRotChk.IsChecked != false;
        var previous = _stockKinds.ToDictionary(
            k => NestGroupKey.From(k.MaterialId, k.ThicknessMm),
            k => k);

        _stockKinds.Clear();
        if (_session.Package is null)
        {
            StockMaterialCards.ItemsSource = null;
            StockPaneEmpty.Visibility = Visibility.Visible;
            _stockGrainUiQuiet = false;
            return;
        }

        var groups = _session.Package.Panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();

        foreach (var group in groups)
        {
            var sample = group.First();
            previous.TryGetValue(group.Key, out var prior);
            var matchedSheet = _session.Package.Sheets.FirstOrDefault(s =>
                NestGroupKey.From(s.Material, s.ThicknessMm).Equals(group.Key));
            var width = prior?.WidthMm
                ?? (matchedSheet is { WidthMm: > 0 } ? matchedSheet.WidthMm : defaultsW);
            var length = prior?.LengthMm
                ?? (matchedSheet is { LengthMm: > 0 } ? matchedSheet.LengthMm : defaultsL);
            var sheetGap = matchedSheet is not null
                ? (matchedSheet.PartClearanceMm > 0 ? matchedSheet.PartClearanceMm : matchedSheet.KerfMm)
                : 0;
            var spacing = prior?.SpacingMm
                ?? (sheetGap > 0 ? sheetGap : defaultsSpacing);
            var border = prior?.BorderMm
                ?? (matchedSheet is { MarginMm: > 0 } ? matchedSheet.MarginMm : defaultsBorder);
            var allowRot = prior?.AllowRotate90 ?? defaultsAllowRot;
            var allowPip = prior?.AllowPartsInPart ?? true;
            var useLeftover = prior?.UseLeftoverPieces ?? false;
            var sheetGrain = prior?.SheetGrainKey
                ?? (group.Any(GrainAlign.HasPartGrain) ? "length" : "none");

            _stockKinds.Add(new StockMaterialKindVm
            {
                MaterialId = group.Key.Material,
                AutoLabel = sample.MaterialGroupLabel,
                Label = !string.IsNullOrWhiteSpace(prior?.Label) ? prior.Label : sample.MaterialGroupLabel,
                ThicknessMm = group.Key.ThicknessMm,
                PanelCount = group.Sum(p => Math.Max(1, p.Quantity)),
                WidthMmText = width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                LengthMmText = length.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                SpacingMmText = spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                BorderMmText = border.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                AllowRotate90 = allowRot,
                SheetGrainKey = sheetGrain,
                AllowPartsInPart = allowPip,
                UseLeftoverPieces = useLeftover,
                LeftoverXMmText = prior is null ? "" : LeftoverBox(prior.LeftoverXMm),
                LeftoverYMmText = prior is null ? "" : LeftoverBox(prior.LeftoverYMm),
                PanelGrainsExpanded = prior?.PanelGrainsExpanded ?? false,
                PanelGrains = group
                    .OrderBy(p => p.DisplayPartName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.PanelId, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new StockPanelGrainRow
                    {
                        PanelId = p.PanelId,
                        DisplayName = p.DisplayPartName,
                        GrainKey = GrainAlign.PartKey(p),
                    })
                    .ToList(),
            });
        }

        StockMaterialCards.ItemsSource = null;
        StockMaterialCards.ItemsSource = _stockKinds.ToList();
        StockPaneEmpty.Visibility = _stockKinds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => _stockGrainUiQuiet = false, DispatcherPriority.Background);
    }

    void OnStockSheetGrainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_stockGrainUiQuiet || _session.Package is null) return;
        InvalidateManufacturingOutputs("sheet grain");
    }

    void OnStockPanelGrainChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_stockGrainUiQuiet || _session.Package is null) return;
        if (sender is not ComboBox { DataContext: StockPanelGrainRow row }) return;
        var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == row.PanelId);
        if (panel is null) return;
        var next = GrainAlign.NormalizePart(row.GrainKey);
        var cur = GrainAlign.NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection);
        if (next == cur) return;
        _session.ReplacePanel(panel.WithGrain(next));
        foreach (var kind in _stockKinds)
        {
            if (kind.PanelGrains.Any(r => r.PanelId == row.PanelId))
                kind.NotifyPanelGrainHeader();
        }
        InvalidateManufacturingOutputs("part grain");
    }

    void OnStockMachineChanged(object sender, SelectionChangedEventArgs e) =>
        SyncMachineSelection(StockMachineCombo.SelectedValue as string);

    void OnStockLabelerChanged(object sender, SelectionChangedEventArgs e)
    {
        _session.LabelerMachineId = SelectedLabelerMachineId();
    }

    void ApplyLabelerSelection(string? id)
    {
        var resolved = string.IsNullOrWhiteSpace(id) ? MachineCatalog.DefaultId : id;
        if (StockLabelerCombo.SelectedValue as string != resolved)
            StockLabelerCombo.SelectedValue = resolved;
        _session.LabelerMachineId = SelectedLabelerMachineId();
    }

    void OnNestMachineChanged(object sender, SelectionChangedEventArgs e) =>
        SyncMachineSelection(MachineCombo.SelectedValue as string);

    void SyncMachineSelection(string? id)
    {
        if (_syncingMachineCombo || string.IsNullOrWhiteSpace(id)) return;
        _syncingMachineCombo = true;
        try
        {
            if (MachineCombo.SelectedValue as string != id)
                MachineCombo.SelectedValue = id;
            if (StockMachineCombo.SelectedValue as string != id)
                StockMachineCombo.SelectedValue = id;
            if (MachineComboModule.SelectedValue as string != id)
                MachineComboModule.SelectedValue = id;
            if (OpsMachineCombo.SelectedValue as string != id)
                OpsMachineCombo.SelectedValue = id;
        }
        finally
        {
            _syncingMachineCombo = false;
        }
    }

    void RefreshGeomRail()
    {
        FeatList.Items.Clear();
        if (_selected is null)
        {
            GeomMeta.Text = "选板后可编辑";
            InspKind.Text = "未选特征";
            DirtyBanner.Visibility = Visibility.Collapsed;
            SmallPanelWarn.Visibility = Visibility.Collapsed;
            return;
        }
        var box = PanelEdit.BBox(_selected);
        var orient = _selected.Orientation;
        GeomMeta.Text =
            $"{_selected.PanelId}" +
            (string.IsNullOrEmpty(_selected.Identity?.ModuleId) ? "" : $" · mod={_selected.Identity!.ModuleId}") + "\n" +
            $"{box.W:0.#} × {box.H:0.#} × {_selected.ThicknessMm:0.#} mm\n" +
            $"材料={_selected.Material ?? "—"} · 面={orient?.MillingFace ?? _selected.Side ?? "—"} · 木纹={_selected.GrainDirection ?? "—"}\n" +
            $"features: {_selected.Features.Count} · 画布拖拽编辑";
        DirtyBanner.Text = _session.ManufacturingDirty
            ? "Nest/CAM 已失效 — 请重新密排后再导出"
            : "";
        DirtyBanner.Visibility = _session.ManufacturingDirty ? Visibility.Visible : Visibility.Collapsed;
        if (PanelEdit.IsSmallPanel(_selected, out var smallReason))
        {
            SmallPanelWarn.Text = $"小板警告：{smallReason}";
            SmallPanelWarn.Visibility = Visibility.Visible;
        }
        else
        {
            SmallPanelWarn.Text = "";
            SmallPanelWarn.Visibility = Visibility.Collapsed;
        }
        foreach (var f in _selected.Features)
        {
            if (PanelEdit.IsHole(f))
                FeatList.Items.Add($"{f.FeatureId} hole D{f.DiameterMm:0.#} @ ({f.X:0.#},{f.Y:0.#}) d={f.DepthMm:0.#}");
            else if (PanelEdit.IsCutout(f))
                FeatList.Items.Add($"{f.FeatureId} throughCutout pts={f.Path?.Count ?? 0}{(f.Through ? " THROUGH" : "")}");
            else if (PanelEdit.IsPocket(f))
            {
                var label = PanelEdit.FeatureDisplayLabel(f);
                var tag = string.IsNullOrEmpty(label) ? "pocket" : $"pocket/{label}";
                FeatList.Items.Add($"{f.FeatureId} {tag} pts={f.Path?.Count ?? f.Profile?.Count ?? 0} d={f.DepthMm:0.#}");
            }
            else if (PanelEdit.IsGroove(f))
                FeatList.Items.Add($"{f.FeatureId} groove pts={f.Path?.Count ?? 0} w={f.WidthMm:0.#} d={f.DepthMm:0.#}");
            else
                FeatList.Items.Add($"{f.FeatureId} {f.Kind}");
        }
        if (FeatList.Items.Count == 0)
            FeatList.Items.Add("无特征（仅外轮廓）");
        if (FeatList.Items.Count > 0 && FeatList.SelectedIndex < 0)
            FeatList.SelectedIndex = 0;
        else
            LoadInspectorFromSelection();
    }

    void OnFeatListChanged(object sender, SelectionChangedEventArgs e) => LoadInspectorFromSelection();

    PanelFeature? SelectedFeature()
    {
        if (_selected is null || FeatList.SelectedIndex < 0) return null;
        if (FeatList.SelectedIndex >= _selected.Features.Count) return null;
        return _selected.Features[FeatList.SelectedIndex];
    }

    void LoadInspectorFromSelection()
    {
        var f = SelectedFeature();
        if (f is null)
        {
            InspKind.Text = "未选特征";
            InspXBox.Text = InspYBox.Text = InspDiaBox.Text = InspDepthBox.Text = InspWidthBox.Text = "";
            return;
        }
        InspKind.Text = $"{f.FeatureId} · {f.Kind}";
        InspXBox.Text = f.X.ToString("0.###");
        InspYBox.Text = f.Y.ToString("0.###");
        InspDiaBox.Text = f.DiameterMm?.ToString("0.###") ?? "";
        InspDepthBox.Text = f.DepthMm?.ToString("0.###") ?? "";
        InspWidthBox.Text = f.WidthMm?.ToString("0.###") ?? "";
    }

    void OnInspectApplyClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var f = SelectedFeature();
        if (f is null)
        {
            SetStatus("先选择特征");
            return;
        }
        double? ParseOpt(string t) => double.TryParse(t, out var v) ? v : null;
        var next = PanelEdit.UpdateFeatureParams(
            _selected,
            f.FeatureId,
            x: ParseOpt(InspXBox.Text),
            y: ParseOpt(InspYBox.Text),
            diameterMm: ParseOpt(InspDiaBox.Text),
            depthMm: ParseOpt(InspDepthBox.Text),
            widthMm: ParseOpt(InspWidthBox.Text));
        var idx = FeatList.SelectedIndex;
        CommitPanel(next);
        if (idx >= 0 && idx < FeatList.Items.Count)
            FeatList.SelectedIndex = idx;
    }

    void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_session.TryUndo()) AfterHistoryRestore();
        else SetStatus("没有可撤销的编辑");
    }

    void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_session.TryRedo()) AfterHistoryRestore();
        else SetStatus("没有可重做的编辑");
    }

    void CommitPanel(PanelPart next)
    {
        _session.ReplacePanel(next);
        _selected = next;
        InvalidateManufacturingOutputs("geom write-back");
        BindPartList(next.PanelId);
        RefreshGeomRail();
        RefreshNestReport();
        UpdateCanvasHint();
        CanvasHost.InvalidateVisual();
    }

    void InvalidateManufacturingOutputs(string reason)
    {
        _nest = null;
        _nestSheetsUsed = [];
        _partInPartSlots = [];
        _guillotineBySheet.Clear();
        _nestHolding.Clear();
        _holdingLayout = [];
        _holdingRegions = [];
        _activeNestSheet = 0;
        _opsOverlay = [];
        ResetProfileBridges();
        ExitBridgeManualMode();
        _showNest = false;
        NcPreview.Text = "";
        SetStatus($"已编辑 · Nest/CAM 已失效（{reason}）· 请回板材页「初始密排」");
        RefreshWorkflowDots();
        RefreshOneClickExport();
        if (AwaitingInitialNest())
            BindPartList(null);
        UpdateCanvasHint();
        CanvasHost.InvalidateVisual();
    }

    void OnGeomMoveClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        CommitPanel(PanelEdit.TranslateFeatures(_selected, 10, 0));
        SetStatus($"特征右移 10mm · {_selected.PanelId}");
    }

    void OnGeomRotClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        CommitPanel(PanelEdit.RotatePanel(_selected, 90));
        SetStatus($"旋转 90° · {_selected.PanelId}");
    }

    void OnGeomHoleClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var box = PanelEdit.BBox(_selected);
        CommitPanel(PanelEdit.AddVerticalHole(_selected, box.MinX + box.W / 2, box.MinY + box.H / 2));
        SetStatus($"已加孔 · {_selected.PanelId}");
    }

    void OnGeomGrooveClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var box = PanelEdit.BBox(_selected);
        var y = box.MinY + box.H * 0.25;
        CommitPanel(PanelEdit.AddVerticalGroove(_selected, [new Point2(box.MinX, y), new Point2(box.MaxX, y)]));
        SetStatus($"已加槽 · {_selected.PanelId}");
    }

    void OnGeomMirrorXClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        CommitPanel(PanelEdit.Mirror(_selected, "X"));
        SetStatus($"镜像 X · {_selected.PanelId}");
    }

    void OnGeomMirrorYClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        CommitPanel(PanelEdit.Mirror(_selected, "Y"));
        SetStatus($"镜像 Y · {_selected.PanelId}");
    }

    void OnGeomDuplicateClick(object sender, RoutedEventArgs e) => DuplicateSelectedPanel();

    void OnPastePanelClick(object sender, RoutedEventArgs e) => PasteClipboardPanel();

    void OnCutPanelClick(object sender, RoutedEventArgs e) => CutSelectedPanel();

    void OnGeomDeleteFeatureClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var f = SelectedFeature();
        if (f is null)
        {
            SetStatus("先选择要删除的特征");
            return;
        }
        CommitPanel(PanelEdit.RemoveFeature(_selected, f.FeatureId));
        SetStatus($"已删特征 {f.FeatureId}");
    }

    void OnDeletePanelClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _session.Package is null) return;
        var id = _selected.PanelId;
        _session.RemovePanel(id);
        InvalidateManufacturingOutputs("delete panel");
        RefreshPartList(selectId: null);
        SetStatus($"已删除板件 {id}");
    }

    void OnPackageGroupRightUp(object sender, MouseButtonEventArgs e)
    {
        // Right-click opens the header menu on every stage; OnPackageGroupMenuOpened
        // decides which items apply (rename kinds on the stock stage, unload elsewhere).
    }

    void OnPackageGroupMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        // Stock stage groups are material kinds (rename); other stages group by package (unload).
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var header = item.Header as string ?? "";
            item.Visibility = header.StartsWith("重命名", StringComparison.Ordinal)
                ? (_stage == "stock" ? Visibility.Visible : Visibility.Collapsed)
                : (_stage == "stock" ? Visibility.Collapsed : Visibility.Visible);
        }
    }

    /// <summary>The ⋯ button on a group header opens the same menu the right-click does.</summary>
    void OnGroupMoreDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement btn) return;
        DependencyObject? d = btn;
        while (d is not null && d is not Expander)
            d = VisualTreeHelper.GetParent(d);
        if (d is not Expander { ContextMenu: { } menu }) return;
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnRenameKindMenuClick(object sender, RoutedEventArgs e)
    {
        if (_stage != "stock") return;
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: DependencyObject target } }) return;
        DependencyObject? d = target;
        while (d is not null && d is not Expander)
            d = VisualTreeHelper.GetParent(d);
        if (d is not Expander expander) return;
        var name = FindDescendant<TextBlock>(expander, t => t.Tag as string == "KindName");
        var edit = FindDescendant<TextBox>(expander, t => t.Tag as string == "KindRename");
        if (name is null || edit is null) return;
        var group = name.DataContext as CollectionViewGroup;
        edit.Text = group?.Name?.ToString() ?? name.Text;
        name.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            edit.Focus();
            edit.SelectAll();
        }, DispatcherPriority.Input);
    }

    static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            var found = FindDescendant(child, match);
            if (found is not null) return found;
        }
        return null;
    }

    void OnRemovePackageClick(object sender, RoutedEventArgs e)
    {
        if (_stage == "stock" || _session.Package is null) return;
        var group = PackageGroupFromMenu(sender);
        var name = group?.Name?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;

        var n = _session.Package.Panels.Count(p => PackageMerge.MatchesKey(p, name));
        if (n == 0) return;
        var confirm = MessageBox.Show(
            this,
            $"移出「{name}」及其 {n} 块板？\n密排和刀路会作废。磁盘上的 .cnjob 不会删除。",
            "移出方案",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_session.TryRemovePackage(name))
        {
            SetStatus($"未找到方案 {name}");
            return;
        }

        ClearManufacturingState();
        if (_session.Package is null)
        {
            _stageChanging = true;
            StageTabs.SelectedIndex = 0;
            _stage = "load";
            _stageChanging = false;
            BindPackage();
            ApplyStageVisibility();
            UpdateStageChrome();
            SetStatus($"已移出方案 {name}");
            return;
        }

        InvalidateManufacturingOutputs("remove package");
        BindPackage();
        SetStatus($"已移出方案 {name} · 共 {_session.Package.Panels.Count} 块板");
    }

    static CollectionViewGroup? PackageGroupFromMenu(object sender)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        var start = menu?.PlacementTarget as DependencyObject ?? sender as DependencyObject;
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement { DataContext: CollectionViewGroup group })
                return group;
        }
        return null;
    }

    void CopySelectedToClipboard()
    {
        if (_stage == "nest" && _nest is { Ok: true } && _session.Package is not null)
        {
            var ids = _nestSelected.Count > 0
                ? _nestSelected.ToList()
                : _selected is not null ? [_selected.PanelId] : [];
            _clipboardNest.Clear();
            foreach (var id in ids)
            {
                var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == id);
                if (panel is null) continue;
                _clipboardNest.Add(PanelEdit.Duplicate(panel, panel.PanelId));
            }
            _clipboardPanel = _clipboardNest.FirstOrDefault();
            if (_clipboardNest.Count == 0)
            {
                SetStatus("先选中要复制的板件");
                return;
            }
            SetStatus($"已复制 {_clipboardNest.Count} 件（Ctrl+V 粘贴到待用区）");
            return;
        }

        if (_selected is null) return;
        _clipboardNest.Clear();
        _clipboardPanel = PanelEdit.Duplicate(_selected, _selected.PanelId);
        SetStatus($"已复制 {_selected.PanelId}（Ctrl+V 粘贴）");
    }

    void CutSelectedPanel()
    {
        if (_selected is null) return;
        CopySelectedToClipboard();
        OnDeletePanelClick(this, new RoutedEventArgs());
    }

    void PasteClipboardPanel()
    {
        if (_session.Package is null)
        {
            SetStatus("剪贴板为空");
            return;
        }

        if (_stage == "nest" && _nest is { Ok: true })
        {
            var sources = _clipboardNest.Count > 0
                ? _clipboardNest.ToList()
                : _clipboardPanel is not null ? [_clipboardPanel] : [];
            if (sources.Count == 0)
            {
                SetStatus("剪贴板为空");
                return;
            }
            PastePanelsIntoHolding(sources);
            return;
        }

        if (_clipboardPanel is null)
        {
            SetStatus("剪贴板为空");
            return;
        }
        var id = _session.NextCopyPanelId(StripCopySuffix(_clipboardPanel.PanelId));
        var copy = PanelEdit.Duplicate(_clipboardPanel, id);
        _session.ReplacePanel(copy);
        InvalidateManufacturingOutputs("paste panel");
        RefreshPartList(selectId: id);
        SetStatus($"已粘贴 {id}");
    }

    void PastePanelsIntoHolding(IReadOnlyList<PanelPart> sources)
    {
        if (_session.Package is null || _nest is not { Ok: true } || sources.Count == 0)
            return;

        var staySheet = _activeNestSheet;
        string? lastId = null;
        foreach (var src in sources)
        {
            var copyId = _session.NextCopyPanelId(src.PanelId);
            var copy = PanelEdit.Duplicate(src, copyId);
            if (!GrainAlign.HasPartGrain(copy)
                && KindHasGrain(copy.Material, copy.ThicknessMm))
                copy = copy.WithGrain("X");
            _session.ReplacePanel(copy);
            var rot = _nestHolding.FirstOrDefault(h => h.PanelId == src.PanelId)?.RotationDeg
                ?? _nest.Placements.FirstOrDefault(p => p.PanelId == src.PanelId)?.RotationDeg
                ?? 0;
            ParkInHolding(copy, rot);
            lastId = copyId;
        }

        if (lastId is not null)
        {
            _nestSelected.Clear();
            _nestSelected.Add(lastId);
            _selected = _session.Package.Panels.FirstOrDefault(p => p.PanelId == lastId);
        }

        _activeNestSheet = staySheet;
        RefreshNestUiKeepSheet(lastId, staySheet);
        SetStatus($"已粘贴 {sources.Count} 件到待用区 · 仍在大板 {staySheet + 1}");
    }

    void RefreshNestUiKeepSheet(string? selectId, int staySheet)
    {
        _syncingNestSelection = true;
        try
        {
            BindPartList(selectId);
            _activeNestSheet = staySheet;
            UpdateNestSheetChrome();
        }
        finally
        {
            _syncingNestSelection = false;
        }
        RefreshGeomRail();
        RefreshNestReport();
        RefreshWorkflowDots();
        UpdateCanvasHint();
        CanvasHost.InvalidateVisual();
    }

    void DuplicateSelectedPanel()
    {
        if (_selected is null || _session.Package is null) return;
        CopySelectedToClipboard();
        var id = _session.NextCopyPanelId(_selected.PanelId);
        var copy = PanelEdit.Duplicate(_selected, id);
        _session.ReplacePanel(copy);
        InvalidateManufacturingOutputs("duplicate panel");
        RefreshPartList(selectId: id);
        SetStatus($"已复制为 {id}");
    }

    static string StripCopySuffix(string id)
    {
        var idx = id.IndexOf("_copy", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? id[..idx] : id;
    }

    void RefreshPartList(string? selectId)
    {
        BindPartList(selectId);
        RefreshGeomRail();
        RefreshNestReport();
        UpdateCanvasHint();
        CanvasHost.InvalidateVisual();
    }

    /// <summary>
    /// Bind panels: stock by material; else Package → Assembly → component.
    /// Nest/ops stay empty until initial nest succeeds.
    /// </summary>
    void BindPartList(string? selectId)
    {
        if (_session.Package is null || AwaitingInitialNest())
        {
            PartList.ItemsSource = null;
            PartList.SelectedItem = null;
            _selected = null;
            ApplyAwaitingNestChrome();
            return;
        }

        LeftRailContent.Visibility = Visibility.Visible;
        var panels = _session.Package.Panels.ToList();
        var quietNest = _stage is "nest" or "ops";
        if (quietNest) _syncingNestSelection = true;
        try
        {
            if (_stage == "stock")
            {
                var rows = PackageMerge.GroupIdenticalStock(panels)
                    .Select(g => new StockPartRow
                    {
                        Representative = g[0],
                        Members = g,
                        MaterialGroupLabel = KindDisplayName(g[0]),
                    })
                    .ToList();
                var view = new ListCollectionView(rows);
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StockPartRow.MaterialGroupLabel)));
                view.SortDescriptions.Add(new SortDescription(nameof(StockPartRow.MaterialGroupLabel), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(StockPartRow.DisplayPartName), ListSortDirection.Ascending));
                PartList.ItemsSource = view;
                var row = rows.FirstOrDefault(r => r.Members.Any(p => p.PanelId == selectId))
                    ?? rows.FirstOrDefault(r => r.Members.Any(p => p.PanelId == _selected?.PanelId))
                    ?? rows.FirstOrDefault();
                _selected = row?.Representative;
                PartList.SelectedItem = row;
                Dispatcher.BeginInvoke(SyncStockKindChecks, DispatcherPriority.Loaded);
                return;
            }

            var tree = new ListCollectionView(panels);
            tree.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PanelPart.DisplayPackage)));
            tree.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PanelPart.DisplayAssembly)));
            tree.SortDescriptions.Add(new SortDescription(nameof(PanelPart.DisplayPackage), ListSortDirection.Ascending));
            tree.SortDescriptions.Add(new SortDescription(nameof(PanelPart.DisplayAssembly), ListSortDirection.Ascending));
            tree.SortDescriptions.Add(new SortDescription(nameof(PanelPart.DisplayPartName), ListSortDirection.Ascending));
            PartList.ItemsSource = tree;

            _selected = panels.FirstOrDefault(p => p.PanelId == selectId)
                ?? panels.FirstOrDefault(p => p.PanelId == _selected?.PanelId)
                ?? panels.FirstOrDefault();
            PartList.SelectedItem = _selected;
            Dispatcher.BeginInvoke(SyncStockKindChecks, DispatcherPriority.Loaded);
        }
        finally
        {
            if (quietNest) _syncingNestSelection = false;
        }
    }

    void OnStockKindNameDown(object sender, MouseButtonEventArgs e)
    {
        if (_stage != "stock" || e.ClickCount < 2 || sender is not TextBlock name)
            return;
        e.Handled = true;
        var edit = FindTaggedSibling<TextBox>(name, "KindRename");
        if (edit is null) return;
        var group = name.DataContext as CollectionViewGroup;
        edit.Text = group?.Name?.ToString() ?? name.Text;
        name.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            edit.Focus();
            edit.SelectAll();
        }, DispatcherPriority.Input);
    }

    void OnStockKindRenameBoxDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void OnStockKindRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox edit)
            CommitKindRename(edit);
    }

    void OnStockKindRenameKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox edit) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitKindRename(edit);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideKindRename(edit);
        }
    }

    void CommitKindRename(TextBox edit)
    {
        if (edit.Visibility != Visibility.Visible) return;
        var group = edit.DataContext as CollectionViewGroup
            ?? (edit.TemplatedParent as GroupItem)?.DataContext as CollectionViewGroup;
        var key = KeyFromStockGroup(group);
        var typed = (edit.Text ?? "").Trim();
        HideKindRename(edit);
        if (key is null) return;
        var vm = _stockKinds.FirstOrDefault(k =>
            NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(key.Value));
        if (vm is null) return;
        vm.Label = string.IsNullOrWhiteSpace(typed) ? vm.AutoLabel : typed;
        ApplyKindRenameSideEffects();
    }

    void HideKindRename(TextBox edit)
    {
        edit.Visibility = Visibility.Collapsed;
        var name = FindTaggedSibling<TextBlock>(edit, "KindName");
        if (name is not null)
            name.Visibility = Visibility.Visible;
    }

    static T? FindTaggedSibling<T>(FrameworkElement start, object tag) where T : FrameworkElement
    {
        var root = start.Parent as DependencyObject ?? start;
        foreach (var fe in FindVisualChildren<T>(root))
        {
            if (Equals(fe.Tag, tag)) return fe;
        }
        if (start.Parent is DependencyObject parent)
        {
            foreach (var fe in FindVisualChildren<T>(parent))
            {
                if (Equals(fe.Tag, tag)) return fe;
            }
        }
        return null;
    }

    void ApplyKindRenameSideEffects()
    {
        if (_stage == "stock" && _session.Package is not null)
            BindPartList(_selected?.PanelId);
        if (NestReportMeta is not null)
            RefreshNestReport();
        if (_stage == "out")
            RefreshExportFiles();
        CanvasHost.InvalidateVisual();
    }

    void OnStockKindPickDown(object sender, MouseButtonEventArgs e)
    {
        if (_stage != "stock" || sender is not FrameworkElement fe)
            return;
        var group = fe.DataContext as CollectionViewGroup
            ?? (fe.TemplatedParent as GroupItem)?.DataContext as CollectionViewGroup;
        var key = KeyFromStockGroup(group);
        if (key is null)
            return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            _pickedStockKinds.Clear();
            _pickedStockKinds.Add(key.Value);
        }
        else if (!_pickedStockKinds.Add(key.Value))
        {
            _pickedStockKinds.Remove(key.Value);
        }
        SyncStockKindChecks();
    }

    NestGroupKey? KeyFromStockGroup(CollectionViewGroup? group)
    {
        if (group is null || _session.Package is null) return null;
        var name = group.Name?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var item in group.Items)
        {
            var panel = item as PanelPart ?? (item as StockPartRow)?.Representative;
            if (panel is not null)
                return NestGroupKey.From(panel.Material, panel.ThicknessMm);
        }
        var hit = _session.Package.Panels.FirstOrDefault(p => p.MaterialGroupLabel == name);
        return hit is null ? null : NestGroupKey.From(hit.Material, hit.ThicknessMm);
    }

    void OnCreatePanelClick(object sender, RoutedEventArgs e) => OpenPanelDraft();

    void OnPartListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_stage != "stock") return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) is null)
            return;
        OpenPanelDraft();
        e.Handled = true;
    }

    void OpenPanelDraft()
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }

        var dlg = new PanelDraftWindow { Owner = this };
        var sample = _selected ?? _session.Package.Panels.FirstOrDefault();
        var material = sample?.Material;
        var thickness = sample?.ThicknessMm > 0 ? sample.ThicknessMm : 18;
        if (_selected is not null)
            dlg.PrepareCreateFrom(_selected);
        else
        {
            dlg.PrepareCreate(
                _session.NextDraftPanelId(),
                name: null,
                material: material,
                thicknessMm: thickness);
        }
        dlg.SetStockKinds(DraftStockKinds(), material, thickness);
        if (dlg.ShowDialog() != true || dlg.ResultPanel is null) return;
        AcceptDraftPanel(dlg.ResultPanel);
    }

    void OnNestCreatePanelClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }
        if (_nest is not { Ok: true })
        {
            SetStatus("请先初始密排，再在密排页创建板件");
            return;
        }

        var sample = CurrentSheetSample();
        var key = ActiveSheetGroupKey();
        var material = sample?.Material ?? key.Material;
        var thickness = sample?.ThicknessMm > 0 ? sample.ThicknessMm
            : key.ThicknessMm > 0 ? key.ThicknessMm : 16;
        if (string.IsNullOrWhiteSpace(material))
        {
            SetStatus("当前大板没有材料，无法创建");
            return;
        }

        var kinds = DraftStockKinds()
            .Where(k => string.Equals(k.Material, material, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(k.ThicknessMm - thickness) < 0.05)
            .ToList();
        if (kinds.Count == 0)
        {
            var label = sample?.MaterialGroupLabel ?? $"{material} · {thickness:0.##}mm";
            kinds.Add(new DraftStockKind(material, thickness, label));
        }

        var dlg = new PanelDraftWindow { Owner = this };
        dlg.PrepareCreate(_session.NextDraftPanelId(), name: null, material, thickness, sample);
        dlg.SetStockKinds(kinds, material, thickness);
        dlg.LockKind();
        if (dlg.ShowDialog() != true || dlg.ResultPanel is null) return;
        AcceptDraftPanelToHolding(dlg.ResultPanel, sample);
    }

    PanelPart? CurrentSheetSample()
    {
        if (_session.Package is null) return null;
        if (_nest is { Ok: true })
        {
            var onSheet = _nest.Placements.FirstOrDefault(p => p.SheetIndex == _activeNestSheet);
            if (onSheet is not null)
            {
                var hit = _session.Package.Panels.FirstOrDefault(p => p.PanelId == onSheet.PanelId);
                if (hit is not null) return hit;
            }
        }
        var key = ActiveSheetGroupKey();
        return _session.Package.Panels.FirstOrDefault(p =>
            NestGroupKey.From(p.Material, p.ThicknessMm).Equals(key));
    }

    bool KindHasGrain(string? material, double thicknessMm)
    {
        if (_session.Package is null) return false;
        if (CurrentSheetGrain() != SheetGrainKind.None) return true;
        return _session.Package.Panels.Any(p =>
            string.Equals(p.Material, material, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(p.ThicknessMm - thicknessMm) < 0.05
            && GrainAlign.HasPartGrain(p));
    }

    void AcceptDraftPanelToHolding(PanelPart panel, PanelPart? sample)
    {
        if (_session.Package is null) return;
        if (panel.Identity is null || string.IsNullOrWhiteSpace(panel.Identity.PackageId))
        {
            panel = panel.WithTree(panel.PanelId, new WorkpieceIdentity
            {
                PackageId = sample?.Identity?.PackageId ?? _session.Package.JobId,
                PackageLabel = sample?.Identity?.PackageLabel ?? sample?.DisplayPackage ?? _session.Package.JobId,
                ProjectId = sample?.Identity?.ProjectId,
                ModuleId = panel.Identity?.ModuleId ?? "Draft",
                WorkpieceId = panel.PanelId,
                Role = panel.Identity?.Role ?? sample?.Identity?.Role,
                SourceFormat = "draft",
            });
        }

        if (!GrainAlign.HasPartGrain(panel)
            && KindHasGrain(panel.Material, panel.ThicknessMm))
            panel = panel.WithGrain("X");

        _session.ReplacePanel(panel);
        ParkInHolding(panel);
        _nestSelected.Clear();
        _nestSelected.Add(panel.PanelId);
        _selected = panel;
        RefreshPartList(selectId: panel.PanelId);
        RefreshStockMaterialCards();
        RefreshNestReport();
        RefreshWorkflowDots();
        CanvasHost.InvalidateVisual();
        SetStatus($"已创建 {panel.DisplayTitle} · 在待用区 · 可拖回当前大板");
    }

    void ParkInHolding(PanelPart panel, double rotDeg = 0)
    {
        var (w, h) = NestDrag.SizeRotated(panel, rotDeg);
        _nestHolding.RemoveAll(hld => hld.PanelId == panel.PanelId);
        _nestHolding.Add(new HeldNestPart
        {
            PanelId = panel.PanelId,
            Material = panel.Material ?? "",
            ThicknessMm = panel.ThicknessMm,
            RotationDeg = rotDeg,
            WidthMm = w,
            HeightMm = h,
        });
    }

    IReadOnlyList<DraftStockKind> DraftStockKinds()
    {
        if (_stockKinds.Count > 0)
        {
            return _stockKinds
                .Select(k => new DraftStockKind(
                    k.MaterialId,
                    k.ThicknessMm,
                    string.IsNullOrWhiteSpace(k.Label) ? k.AutoLabel : k.Label))
                .ToList();
        }

        if (_session.Package is null) return [];
        return _session.Package.Panels
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .Select(g => new DraftStockKind(
                g.Key.Material,
                g.Key.ThicknessMm,
                g.First().MaterialGroupLabel))
            .ToList();
    }

    void AcceptDraftPanel(PanelPart panel)
    {
        if (_session.Package is null) return;
        var sample = _session.Package.Panels.FirstOrDefault();
        if (panel.Identity is null || string.IsNullOrWhiteSpace(panel.Identity.PackageId))
        {
            panel = panel.WithTree(panel.PanelId, new WorkpieceIdentity
            {
                PackageId = sample?.Identity?.PackageId ?? _session.Package.JobId,
                PackageLabel = sample?.Identity?.PackageLabel ?? sample?.DisplayPackage ?? _session.Package.JobId,
                ProjectId = sample?.Identity?.ProjectId,
                ModuleId = panel.Identity?.ModuleId ?? "Draft",
                WorkpieceId = panel.PanelId,
                Role = panel.Identity?.Role ?? sample?.Identity?.Role,
                SourceFormat = "draft",
            });
        }

        _session.ReplacePanel(panel);
        InvalidateManufacturingOutputs("create panel");
        RefreshPartList(selectId: panel.PanelId);
        RefreshStockMaterialCards();
        SetStatus($"已加入板件 {panel.DisplayTitle} · 请确认板材后重新密排");
    }

    static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T hit) return hit;
        }
        return null;
    }

    void SyncStockKindChecks()
    {
        if (MergeKindsBtn is null || PartList is null) return;
        foreach (var cb in FindVisualChildren<CheckBox>(PartList))
        {
            if (!Equals(cb.Tag, "StockKindPick")) continue;
            var key = KeyFromStockGroup(cb.DataContext as CollectionViewGroup);
            cb.IsChecked = key is not null && _pickedStockKinds.Contains(key.Value);
        }
        MergeKindsBtn.IsEnabled = _stage == "stock" && _pickedStockKinds.Count >= 2;
    }

    void OnMergeKindsClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _pickedStockKinds.Count < 2)
        {
            SetStatus("请先 Ctrl 点选至少两个种类");
            return;
        }

        var options = _pickedStockKinds
            .Select(key =>
            {
                var members = _session.Package.Panels.Where(p => MaterialCorrect.SameKind(p, key)).ToList();
                var sample = members.FirstOrDefault();
                return new MaterialKindOption
                {
                    Key = key,
                    Label = sample is null ? key.ToString() : KindDisplayName(sample),
                    PanelCount = members.Sum(p => Math.Max(1, p.Quantity)),
                };
            })
            .OrderByDescending(o => o.PanelCount)
            .ToList();

        var selectedPanels = _session.Package.Panels
            .Where(p => _pickedStockKinds.Contains(NestGroupKey.From(p.Material, p.ThicknessMm)))
            .ToList();

        var dlg = new MaterialMergeWindow(options, selectedPanels) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ChosenKey is not { } target)
            return;

        if (!_session.TryMergeMaterialKinds(options.Select(o => o.Key).ToList(), target, dlg.BlindPolicy))
        {
            SetStatus("合并失败");
            return;
        }

        _pickedStockKinds.Clear();
        InvalidateManufacturingOutputs("材料合并");
        RefreshStockMaterialCards();
        BindPartList(_selected?.PanelId);
        RefreshGeomRail();
        var label = options.First(o => o.Key.Equals(target)).Label;
        var count = _session.Package.Panels
            .Where(p => MaterialCorrect.SameKind(p, target))
            .Sum(p => Math.Max(1, p.Quantity));
        SetStatus($"已合并为 {label} · {count} 件");
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    void OnLockPlaceClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _nest is not { Ok: true })
        {
            SetStatus("无摆位可锁定");
            return;
        }
        var id = _selected.PanelId;
        if (!_nest.Placements.Any(p => p.PanelId == id))
        {
            SetStatus("选中板未排版");
            return;
        }
        if (_locked.Contains(id))
        {
            _locked.Remove(id);
            LockPlaceBtn.Content = "锁定摆位";
            SetStatus($"已解锁摆位 · {id}");
        }
        else
        {
            _locked.Add(id);
            LockPlaceBtn.Content = "解锁摆位";
            SetStatus($"已锁定摆位 · {id}");
        }
        CanvasHost.InvalidateVisual();
    }

    async void OnApplyNestSettingsClick(object sender, RoutedEventArgs e) =>
        await RunNestAsync(withNc: false);

    string SelectedNestEnginePreference() =>
        NestEngineCombo.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && !string.IsNullOrWhiteSpace(tag)
                ? tag
                : "nfp";

    bool UsesTrueShapeNest() =>
        _nest?.Engine.StartsWith("deepnest_", StringComparison.OrdinalIgnoreCase) == true
        || _nest?.Engine.StartsWith("clipper_nfp", StringComparison.OrdinalIgnoreCase) == true;

    async Task RunNestAsync(bool withNc)
    {
        if (_nestBusy) return;
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }

        _nestBusy = true;
        _nestCts?.Dispose();
        _nestCts = new CancellationTokenSource();
        var cancelToken = _nestCts.Token;
        SetNestBusyUi(true);
        BeginNestProgress("密排准备中…");
        UsageLog.LogActionStart("nest.run", new Dictionary<string, object?>
        {
            ["withNc"] = withNc,
            ["panelCount"] = _session.Package.Panels.Count,
            ["machineId"] = SelectedMachineId(),
        });
        try
        {
            RefreshStockMaterialCards();
            var allowRot = _stockKinds.Count > 0
                ? _stockKinds.Any(k => k.AllowRotate90)
                : NestAllowRotChk.IsChecked == true;
            var border = _stockKinds.Count > 0
                ? _stockKinds.Max(k => k.BorderMm)
                : ParseMm(NestBorderBox.Text, 15);
            var spacing = _stockKinds.Count > 0
                ? _stockKinds.Min(k => k.SpacingMm)
                : ParseMm(NestSpacingBox.Text, 12);
            var enginePreference = SelectedNestEnginePreference();
            var settings = new NestSettings
            {
                MarginMm = border,
                ClearanceMm = spacing,
                AllowRotation = allowRot,
                GrainLock = true,
                PreferLockedPlacements = true,
            };
            var consistency = settings.ValidateConsistency();
            if (consistency.Count > 0)
                SetStatus("密排参数警告: " + string.Join(", ", consistency), StatusKind.Warning);

            var sheets = BuildNestSheetQueue(border);
            var prevPlaces = _nest?.Placements.ToDictionary(p => p.PanelId, p => p);
            var panels = _session.Package.Panels.ToList();
            var progress = new Progress<NestProgressReport>(OnNestProgress);

            SetStatus("密排计算中…");
            INestingEngine advanced = enginePreference is "deepnest" or "deepnest_next"
                ? new DeepnestPreviewNestingEngine()
                : new ClipperNfpNestingEngine();
            var advancedTimeout = panels.Count > 80
                ? TimeSpan.FromSeconds(45)
                : TimeSpan.FromSeconds(25);

            var packedPair = await Task.Run(() =>
                new NestEngineRouter(advanced: advanced).Run(
                    new NestEngineRequest
                    {
                        Panels = panels,
                        Settings = settings,
                        StockTemplates = sheets,
                        SizeOf = SizeOf,
                        EnginePreference = enginePreference,
                        AdvancedTimeout = advancedTimeout,
                        Progress = progress,
                    },
                    cancelToken)).ConfigureAwait(true);

            var packed = packedPair.Result;
            var engineLog = packedPair.Log;
            NestProgress.Value = NestProgress.Maximum;

            _nestSheetsUsed = packed.SheetsUsed?.Count > 0
                ? packed.SheetsUsed.ToList()
                : [];
            _guillotineBySheet.Clear();
            _nestHolding.Clear();
            _holdingLayout = [];
            _holdingRegions = [];
            _activeNestSheet = 0;
            ResetProfileBridges();
            _nest = new StartNestingReply
            {
                Ok = true,
                Engine = packed.Engine,
                SheetCount = packed.SheetCount > 0
                    ? packed.SheetCount
                    : Math.Max(1, _nestSheetsUsed.Count),
            };
            if (!string.IsNullOrWhiteSpace(engineLog.FallbackReason))
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = "engine_fallback",
                    Message = $"{engineLog.AttemptedEngine} → {engineLog.SelectedEngine}: {engineLog.FallbackReason} ({engineLog.ElapsedMs}ms)",
                });
            }
            else
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = "engine",
                    Message = $"{engineLog.SelectedEngine} · {engineLog.ElapsedMs}ms · util~{engineLog.UtilizationHintPct:0.0}%",
                });
            }
            _partInPartSlots = packed.PartInPartSlots?.ToList() ?? [];
            if (_partInPartSlots.Count > 0)
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = "parts_in_part",
                    Message = $"parts in part：{_partInPartSlots.Count} 件放入开窗空洞",
                });
            }
            else
            {
                foreach (var kind in _stockKinds.Where(k => k.AllowPartsInPart))
                {
                    _nest.Warnings.Add(new NestWarningMsg
                    {
                        Code = "parts_in_part_none",
                        Message = $"{kind.Label}: 已启用 parts in part，但无合适开窗/子件可嵌套",
                    });
                }
            }
            _nest.Unplaced.AddRange(packed.Unplaced);
            foreach (var p in packed.Placements)
            {
                _nest.Placements.Add(new NestPlacementMsg
                {
                    PanelId = p.PanelId,
                    SheetIndex = p.SheetIndex,
                    OffsetX = p.OffsetX,
                    OffsetY = p.OffsetY,
                    RotationDeg = p.RotationDeg,
                });
            }
            foreach (var r in packed.UnplacedReasons)
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = r.Code,
                    Message = $"{r.PanelId}: {r.Message}",
                    PanelIdA = r.PanelId,
                });
            }
            foreach (var g in packed.GroupReports)
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = "group_report",
                    Message =
                        $"{g.Key}: placed {g.PlacedCount}/{g.PartCount} · sheets {g.SheetCount} · util {g.UtilizationPct:0.0}%",
                });
            }
            if (prevPlaces is not null && _locked.Count > 0)
            {
                foreach (var place in _nest.Placements)
                {
                    if (!_locked.Contains(place.PanelId)) continue;
                    if (!prevPlaces.TryGetValue(place.PanelId, out var old)) continue;
                    place.OffsetX = old.OffsetX;
                    place.OffsetY = old.OffsetY;
                    place.RotationDeg = old.RotationDeg;
                    place.SheetIndex = old.SheetIndex;
                }
            }

            var collisions = NestValidator.FindPolygonCollisions(
                _session.Package.Panels,
                CurrentNestPlacements(),
                spacing,
                PipIgnorePairs());
            foreach (var c in collisions)
            {
                _nest.Warnings.Add(new NestWarningMsg
                {
                    Code = "poly_gap",
                    Message = $"polygon spacing/collision {c.PanelIdA} × {c.PanelIdB} on sheet {c.SheetIndex}",
                    PanelIdA = c.PanelIdA,
                    PanelIdB = c.PanelIdB,
                    SheetIndex = c.SheetIndex,
                });
            }

            var gate = NestExportGate.Check(
                _session.Package.Panels,
                CurrentNestPlacements(),
                spacing,
                allowAabbOverlap: UsesTrueShapeNest(),
                partInPartSlots: _partInPartSlots);
            if (!gate.Ok)
            {
                foreach (var err in gate.Errors.Take(12))
                {
                    _nest.Warnings.Add(new NestWarningMsg
                    {
                        Code = "export_gate",
                        Message = err,
                    });
                }
            }

            _showNest = true;
            ResetSimView();
            FocusRetargetedPlacements();
            if (_stage != "nest" && _stage != "ops")
            {
                _stageChanging = true;
                StageTabs.SelectedIndex = 2;
                _stage = "nest";
                _stageChanging = false;
            }
            ApplyStageVisibility();
            UpdateStageChrome();
            BindPartList(_selected?.PanelId);
            UpdateCanvasHint();
            RebuildOpsOverlay();
            _session.MarkManufacturingClean();
            CanvasHost.InvalidateVisual();

            var opsNote = "";
            var ncNote = "";
            if (withNc)
            {
                try
                {
                    var profile = ActiveProfileForCam();
                    var opsForNc = _opsOverlay.ToList();
                    var nc = NcEmitter.OpsToNc(opsForNc, profile, recipe: CurrentPostRecipe());
                    NcPreview.Text = nc;
                    ncNote = $" · NC {profile.Id} lines={nc.Split('\n').Length}";
                    opsNote = $" · ops c={opsForNc.Count(o => o.Op == "contour")} d={opsForNc.Count(o => o.Op == "drill")} g={opsForNc.Count(o => o.Op == "groove")}";
                }
                catch (Exception ex)
                {
                    opsNote = " · ops/nc err: " + ex.Message;
                    NcPreview.Text = "// " + ex.Message;
                }
            }

            var warn = _nest.Warnings.Count;
            var hardWarnings = _nest.Warnings
                .Where(w => w.Code is not ("engine" or "engine_fallback" or "parts_in_part" or "parts_in_part_none" or "group_report"))
                .ToList();
            var warnTxt = hardWarnings.Count == 0
                ? " · 校验通过"
                : $" · 警告 {hardWarnings.Count}: " + string.Join("; ", hardWarnings.Take(3).Select(w => w.Message));
            SetStatus(
                $"密排完成 · 已排 {_nest.Placements.Count} 件 · {_nest.SheetCount} 张大板 · 未排 {_nest.Unplaced.Count}{warnTxt}{opsNote}{ncNote}",
                _nest.Unplaced.Count > 0 || hardWarnings.Count > 0 ? StatusKind.Warning : StatusKind.Success);
            if (_nest.Unplaced.Count > 0)
            {
                ShowToast($"有 {_nest.Unplaced.Count} 件没有排进大板",
                    "右侧「未排 / 警告」列出了原因；可放大板尺寸、允许旋转，或把余料加入密排。",
                    StatusKind.Warning);
            }
            else
            {
                ShowToast($"密排完成 · {_nest.SheetCount} 张大板 · {_nest.Placements.Count} 件",
                    hardWarnings.Count == 0 ? "校验通过，可以进入刀路计算。" : $"{hardWarnings.Count} 条警告，见右侧「未排 / 警告」。",
                    hardWarnings.Count == 0 ? StatusKind.Success : StatusKind.Warning,
                    "去计算刀路", () => GoToStage("ops"));
            }
            UsageLog.LogActionResult("nest.run", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["withNc"] = withNc,
                ["engine"] = _nest.Engine,
                ["placedCount"] = _nest.Placements.Count,
                ["sheetCount"] = _nest.SheetCount,
                ["unplacedCount"] = _nest.Unplaced.Count,
                ["warningCount"] = warn,
                ["warnings"] = _nest.Warnings.Take(12).Select(w => new Dictionary<string, object?>
                {
                    ["code"] = w.Code,
                    ["message"] = w.Message,
                    ["panelIdA"] = w.PanelIdA,
                    ["panelIdB"] = w.PanelIdB,
                }).ToList(),
                ["unplaced"] = _nest.Unplaced.Take(20).ToList(),
                ["opsNote"] = opsNote,
                ["ncNote"] = ncNote,
                ["allowRotation"] = allowRot,
                ["borderMm"] = border,
                ["spacingMm"] = spacing,
                ["machineId"] = SelectedMachineId(),
            });
            RefreshNestReport();
            RebuildOpsOverlay();
            RefreshWorkflowDots();
            CanvasHost.InvalidateVisual();
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            SetStatus(_nest is { Ok: true }
                ? "已取消密排 · 保留上一次结果"
                : "已取消密排", StatusKind.Warning);
            UsageLog.LogActionResult("nest.run", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["withNc"] = withNc,
                ["cancelled"] = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("密排失败: " + ex.Message, StatusKind.Error);
            UsageLog.LogActionResult("nest.run", new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["withNc"] = withNc,
            }, error: ex.Message);
        }
        finally
        {
            EndNestProgress();
            SetNestBusyUi(false);
            _nestBusy = false;
            _nestCts?.Dispose();
            _nestCts = null;
        }
        // Keep the worker warm, but only after the busy state is released: awaiting it inside
        // the try block kept the buttons disabled and the progress bar full for seconds after
        // the nest had actually finished.
        _ = RefreshWorkerAsync();
    }

    CancellationTokenSource? _nestCts;

    void OnNestCancelClick(object sender, RoutedEventArgs e)
    {
        if (_nestCts is null || _nestCts.IsCancellationRequested) return;
        _nestCts.Cancel();
        NestCancelBtn.IsEnabled = false;
        SetStatus("正在停止密排…", StatusKind.Busy);
    }

    void BeginNestProgress(string message)
    {
        NestProgress.Visibility = Visibility.Visible;
        NestProgress.IsIndeterminate = true;
        NestProgress.Minimum = 0;
        NestProgress.Maximum = 100;
        NestProgress.Value = 0;
        NestCancelBtn.IsEnabled = true;
        NestCancelBtn.Visibility = Visibility.Visible;
        SetStatus(message);
    }

    void EndNestProgress()
    {
        NestProgress.IsIndeterminate = false;
        NestProgress.Value = 0;
        NestProgress.Visibility = Visibility.Collapsed;
        NestCancelBtn.Visibility = Visibility.Collapsed;
    }

    void OnNestProgress(NestProgressReport report)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnNestProgress(report));
            return;
        }

        // Reports are marshalled with BeginInvoke and can land after EndNestProgress();
        // a straggler used to re-show a full green bar that never went away.
        if (!_nestBusy)
            return;

        NestProgress.Visibility = Visibility.Visible;
        if (report.Total > 0)
        {
            NestProgress.IsIndeterminate = false;
            NestProgress.Maximum = Math.Max(1, report.Total);
            NestProgress.Value = Math.Clamp(report.Done, 0, NestProgress.Maximum);
        }
        else
        {
            NestProgress.IsIndeterminate = true;
        }

        if (!string.IsNullOrWhiteSpace(report.Message))
            SetStatus(report.Message, StatusKind.Busy);
    }

    void SetNestBusyUi(bool busy)
    {
        var enable = !busy;
        StockInitialNestBtn.IsEnabled = enable;
        NestApplyBtn.IsEnabled = enable;
        NestStabilizeBtn.IsEnabled = enable;
        NestGuillotineBtn.IsEnabled = enable;
        if (NestGuillotineAllBtn is not null)
            NestGuillotineAllBtn.IsEnabled = enable;
        NestVerifyPolyBtn.IsEnabled = enable;
    }

    List<NestSheetSpec> BuildNestSheetQueue(double border)
    {
        if (_stockKinds.Count == 0)
            RefreshStockMaterialCards();

        var queue = new List<NestSheetSpec>();
        var fallbackSpacing = ParseMm(NestSpacingBox.Text, 12);
        var fallbackAllowRot = NestAllowRotChk.IsChecked != false;
        if (_stockKinds.Count > 0)
        {
            foreach (var kind in _stockKinds)
            {
                if (kind.HasLeftoverSheet)
                {
                    queue.Add(NestSheetSpec.LeftoverAtOrigin(
                        kind.LeftoverXMm,
                        kind.LeftoverYMm,
                        kind.WidthMm,
                        kind.LengthMm,
                        kind.BorderMm,
                        new NestSheetSpec
                        {
                            SpacingMm = kind.SpacingMm,
                            AllowRotation = kind.AllowRotate90,
                            AllowPartsInPart = kind.AllowPartsInPart,
                            Material = kind.MaterialId,
                            ThicknessMm = kind.ThicknessMm,
                            SheetGrain = kind.SheetGrain,
                        }));
                }

                var matched = _session.Package?.Sheets.FirstOrDefault(s =>
                    NestGroupKey.From(s.Material, s.ThicknessMm)
                        .Equals(NestGroupKey.From(kind.MaterialId, kind.ThicknessMm)));
                queue.Add(new NestSheetSpec
                {
                    WidthMm = kind.WidthMm,
                    LengthMm = kind.LengthMm,
                    BorderMm = kind.BorderMm,
                    SpacingMm = kind.SpacingMm,
                    AllowRotation = kind.AllowRotate90,
                    AllowPartsInPart = kind.AllowPartsInPart,
                    Label = kind.Label,
                    Material = kind.MaterialId,
                    ThicknessMm = kind.ThicknessMm,
                    SheetGrain = kind.SheetGrain,
                    Blocked = matched?.DefectRegions.Select(d => new NestBlockedRect
                    {
                        MinX = d.MinX, MinY = d.MinY, MaxX = d.MaxX, MaxY = d.MaxY,
                    }).ToList() ?? [],
                });
            }
        }
        else
        {
            // Blank template (ThicknessMm=0): GroupedBlfNester clones per material/thickness group.
            queue.Add(new NestSheetSpec
            {
                WidthMm = ParseMm(StockWidthBox.Text, 1200),
                LengthMm = ParseMm(StockLengthBox.Text, 2400),
                BorderMm = border,
                SpacingMm = fallbackSpacing,
                AllowRotation = fallbackAllowRot,
                Label = "STOCK",
                Material = null,
                ThicknessMm = 0,
            });
        }

        return queue;
    }

    MachineProfile ActiveProfileForCam()
    {
        var p = MachineCatalog.Get(SelectedMachineId());
        var tool = _library.Tools.FirstOrDefault(t => t.Id == _activeToolId);
        return new MachineProfile
        {
            Id = p.Id,
            Name = p.Name,
            Dialect = p.Dialect,
            ProgramEnd = p.ProgramEnd,
            SafeZMm = p.SafeZMm,
            FeedXyMmMin = tool?.FeedXyMmMin > 0 ? tool.FeedXyMmMin : p.FeedXyMmMin,
            FeedZMmMin = tool?.FeedZMmMin > 0 ? tool.FeedZMmMin : p.FeedZMmMin,
            SpindleRpm = tool?.SpindleRpm > 0 ? tool.SpindleRpm : p.SpindleRpm,
            ToolDiameterMm = tool?.DiameterMm > 0 ? tool.DiameterMm : p.ToolDiameterMm,
            ContourDepthMm = p.ContourDepthMm,
            ContourStepdownMm = p.ContourStepdownMm,
            DrillPeckMm = p.DrillPeckMm,
            EnableContour = true,
            EnableDrill = true,
            EnableGroove = true,
            OriginNote = p.OriginNote,
        };
    }

    void RegenerateNcFromCurrentOps()
    {
        if (_opsOverlay.Count == 0 || _nest is not { Ok: true })
        {
            _exportFiles = [];
            _exportSelected = null;
            if (OutFileList is not null)
            {
                _syncingExportFiles = true;
                OutFileList.ItemsSource = _exportFiles;
                _syncingExportFiles = false;
            }
            if (NcPreview is not null) NcPreview.Text = "";
            RefreshExportButtons();
            RefreshWorkflowDots();
            RefreshPreflightMeta();
            return;
        }
        RefreshExportFiles();
    }

    async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedWork("打开另一份方案")) return;
        var dlg = new OpenFileDialog
        {
            Filter = "OmniCam job|*.cnjob;*.zip;*.json;manifest.json|Manufacturing snapshot (*.cnjob)|*.cnjob|WoodJob zip (*.zip)|*.zip|JSON package (*.json)|*.json|All|*.*",
            Title = "打开方案：Fusion .cnjob / manufacturing-snapshot（也支持 woodjob / cut-package）",
        };
        if (dlg.ShowDialog() != true) return;
        await OpenPackagePathAsync(dlg.FileName);
    }

    async Task OpenPackagePathAsync(string path)
    {
        var result = _session.OpenPackageFile(path);
        if (!result.Ok)
        {
            SetStatus("导入失败: " + string.Join("; ", result.Errors.Select(x => $"{x.Path}: {x.Message}")), StatusKind.Error);
            ShowImportDialog(false, "载入方案", Path.GetFileName(path), result);
            return;
        }
        ClearManufacturingState();
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        _stageChanging = true;
        StageTabs.SelectedIndex = 0;
        _stage = "load";
        _stageChanging = false;
        ApplyStageVisibility();
        UpdateStageChrome();
        RememberRecentFile(path, "package");
        MarkWorkSaved();
        SetStatus($"已打开 {Path.GetFileName(path)} · {_session.Package!.Panels.Count} 块板 · {_session.Package.SchemaName}", StatusKind.Success);
        ShowImportDialog(true, "载入方案", Path.GetFileName(path), result);
        await RefreshWorkerAsync();
    }

    /// <summary>Open / import guard: offer to save first when the current work would be lost.</summary>
    bool ConfirmDiscardUnsavedWork(string action)
    {
        if (!HasUnsavedWork()) return true;
        var r = MessageBox.Show(this,
            $"当前工程「{_session.ResolvedProjectName}」有未保存的改动。\n\n{action}前先保存吗？",
            "未保存的改动",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return TrySaveProjectInteractive();
        return true;
    }

    // ----- recent files ---------------------------------------------------------------

    const int RecentFilesMax = 10;

    void RememberRecentFile(string path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        _library.RecentFiles.RemoveAll(r => string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase));
        _library.RecentFiles.Insert(0, new RecentFile { Path = full, Kind = kind, OpenedAt = DateTimeOffset.Now.ToString("o") });
        if (_library.RecentFiles.Count > RecentFilesMax)
            _library.RecentFiles.RemoveRange(RecentFilesMax, _library.RecentFiles.Count - RecentFilesMax);
        try
        {
            WorkshopLibraryStore.Save(_library);
        }
        catch
        {
            // recent list is a convenience; never let it break an open
        }
        RefreshRecentUi();
    }

    void RefreshRecentUi()
    {
        if (RecentMenu is null) return;
        RecentMenu.Items.Clear();
        var items = _library.RecentFiles.Where(r => !string.IsNullOrWhiteSpace(r.Path)).ToList();
        if (items.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "（暂无）", IsEnabled = false });
        }
        else
        {
            foreach (var r in items)
            {
                var exists = File.Exists(r.Path) || Directory.Exists(r.Path);
                var mi = new MenuItem
                {
                    Header = $"{KindGlyph(r.Kind)} {Path.GetFileName(r.Path)}",
                    InputGestureText = Path.GetDirectoryName(r.Path),
                    Tag = r,
                    IsEnabled = exists,
                    ToolTip = exists ? r.Path : r.Path + "（文件已不存在）",
                };
                mi.Click += OnRecentFileClick;
                RecentMenu.Items.Add(mi);
            }
            RecentMenu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清除列表" };
            clear.Click += (_, _) => { _library.RecentFiles.Clear(); PersistLibrary(); RefreshRecentUi(); };
            RecentMenu.Items.Add(clear);
        }

        if (EmptyRecentPanel is null) return;
        EmptyRecentPanel.Children.Clear();
        var recent = items.Where(r => File.Exists(r.Path) || Directory.Exists(r.Path)).Take(5).ToList();
        EmptyRecentPanel.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (recent.Count == 0) return;
        EmptyRecentPanel.Children.Add(new TextBlock
        {
            Text = "最近打开",
            Style = (Style)FindResource("FieldLabel"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var r in recent)
        {
            var b = new Button
            {
                Content = $"{KindGlyph(r.Kind)} {Path.GetFileName(r.Path)}",
                Style = (Style)FindResource("LinkButton"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 1),
                Tag = r,
                ToolTip = r.Path,
            };
            b.Click += OnRecentFileClick;
            EmptyRecentPanel.Children.Add(b);
        }
    }

    static string KindGlyph(string kind) => kind switch
    {
        "project" => "工程",
        "anc" => ".anc",
        _ => "方案",
    };

    async void OnRecentFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecentFile r }) return;
        if (!File.Exists(r.Path) && !Directory.Exists(r.Path))
        {
            SetStatus($"文件已不存在：{r.Path}", StatusKind.Warning);
            _library.RecentFiles.RemoveAll(x => string.Equals(x.Path, r.Path, StringComparison.OrdinalIgnoreCase));
            PersistLibrary();
            RefreshRecentUi();
            return;
        }
        if (!ConfirmDiscardUnsavedWork("打开最近文件")) return;
        switch (r.Kind)
        {
            case "project": OpenProjectPath(r.Path); break;
            case "anc": await ImportAncPathAsync(r.Path); break;
            default: await OpenPackagePathAsync(r.Path); break;
        }
    }

    async void OnAddPackageClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "OmniCam job|*.cnjob;*.zip;*.json;manifest.json|Manufacturing snapshot (*.cnjob)|*.cnjob|WoodJob zip (*.zip)|*.zip|JSON package (*.json)|*.json|All|*.*",
            Title = "加入方案（可多选，与当前板件并列密排）",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        var names = new List<string>();
        PackageImportResult? last = null;
        foreach (var path in dlg.FileNames)
        {
            last = _session.Package is null && names.Count == 0
                ? _session.OpenPackageFile(path)
                : _session.AddPackageFile(path);
            if (!last.Ok)
            {
                SetStatus("加入失败: " + string.Join("; ", last.Errors.Select(x => $"{x.Path}: {x.Message}")));
                ShowImportDialog(false, "加入方案", Path.GetFileName(path), last);
                return;
            }
            names.Add(Path.GetFileName(path));
            RememberRecentFile(path, "package");
        }

        ClearManufacturingState();
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        _stageChanging = true;
        StageTabs.SelectedIndex = 0;
        _stage = "load";
        _stageChanging = false;
        ApplyStageVisibility();
        UpdateStageChrome();
        SetStatus($"已加入 {names.Count} 份 · 共 {_session.Package!.Panels.Count} 块板 · {string.Join(", ", names)}");
        ShowImportDialog(true, "加入方案", string.Join(", ", names), last);
        await RefreshWorkerAsync();
    }

    async void OnImportAncClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedWork("从 .anc 反推")) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Troy OSAI (*.anc;*.nc)|*.anc;*.nc|All|*.*",
            Title = "从机台 .anc / .nc 反推板件",
        };
        if (dlg.ShowDialog() != true) return;
        await ImportAncPathAsync(dlg.FileName);
    }

    async Task ImportAncPathAsync(string path)
    {
        string nc;
        try
        {
            nc = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            SetStatus("无法读取: " + ex.Message);
            MessageBox.Show(this, ex.Message, "从 .anc 反推", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = NcReverse.FromText(nc);
        if (result.Panels.Count == 0)
        {
            var why = result.Warnings.Count > 0 ? string.Join(", ", result.Warnings) : "没有认出闭合外形";
            SetStatus("反推失败: " + why);
            MessageBox.Show(this, "没有还原出板件。\n" + why, "从 .anc 反推", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var jobId = Path.GetFileNameWithoutExtension(path);
        _session.AcceptPackage(NcReverse.ToPackage(result, jobId), path);
        ClearManufacturingState();
        _module = "remnants";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        RefreshRecutPanelList();
        RememberRecentFile(path, "anc");
        MarkWorkSaved();
        SetStatus($"从 {Path.GetFileName(path)} 反推 {result.Panels.Count} 块板 · 勾选后点「重切勾选的板」", StatusKind.Success);
        await RefreshWorkerAsync();
    }

    async void OnRecutSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案或从 .anc 反推");
            return;
        }
        var picked = RecutPanelList.Items.OfType<RecutRow>().Where(r => r.Selected).Select(r => r.PanelId).ToHashSet(StringComparer.Ordinal);
        if (picked.Count == 0)
        {
            SetStatus("请至少勾选一块要重切的板");
            return;
        }

        var panels = _session.Package.Panels
            .Where(p => picked.Contains(p.PanelId))
            .Select(p => p.WithQuantity(1))
            .ToList();
        var pkg = _session.Package.WithPanels(panels);
        _session.AcceptPackage(pkg, _session.SourcePath);
        ClearManufacturingState();
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        _stageChanging = true;
        StageTabs.SelectedIndex = 1;
        _stage = "stock";
        _stageChanging = false;
        ApplyStageVisibility();
        UpdateStageChrome();
        SetStatus($"补板 {panels.Count} 块 · 数量均为 1 · 确认板材后密排导出");
        await RefreshWorkerAsync();
    }


    void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardUnsavedWork("打开另一个工程")) return;
        var dlg = new OpenFileDialog
        {
            Filter = "OmniCam project|project.db;*.db|All|*.*",
            Title = "打开工程",
        };
        if (dlg.ShowDialog() != true) return;
        OpenProjectPath(dlg.FileName);
    }

    void OpenProjectPath(string projectPath)
    {
        var doc = _store.Load(projectPath);
        if (doc is null)
        {
            SetStatus("工程为空或无法读取");
            ShowImportDialog(false, "打开工程", Path.GetFileName(projectPath), null, "工程为空或无法读取");
            return;
        }
        var result = _session.OpenPackageJson(
            doc.PackageJson,
            projectPath,
            doc.SourceSnapshotJson);
        if (!result.Ok)
        {
            SetStatus("工程中的方案无效: " + string.Join("; ", result.Errors.Select(x => x.Message)), StatusKind.Error);
            ShowImportDialog(false, "打开工程", Path.GetFileName(projectPath), result);
            return;
        }

        ClearManufacturingState();
        _session.MachineId = doc.MachineId;
        _session.SetProjectDbPath(projectPath);
        _session.ProjectName = string.IsNullOrWhiteSpace(doc.Name) ? null : doc.Name;
        if (string.IsNullOrWhiteSpace(_session.ProjectName))
            _session.SuggestProjectName(_session.Package?.JobId, projectPath);
        SyncMachineSelection(doc.MachineId);

        var session = ProjectSessionCodec.Deserialize(doc.SessionJson);
        if (session is not null)
            ApplyProjectSession(session);

        var places = SqliteProjectStore.DeserializeNest(doc.NestPlacementsJson);
        if (places.Count > 0)
        {
            _nest = new StartNestingReply
            {
                Ok = true,
                Engine = session?.NestEngine ?? "restored",
                SheetCount = session is { NestSheetCount: > 0 }
                    ? session.NestSheetCount
                    : places.Max(p => p.SheetIndex) + 1,
            };
            foreach (var p in places)
            {
                _nest.Placements.Add(new NestPlacementMsg
                {
                    PanelId = p.PanelId,
                    SheetIndex = p.SheetIndex,
                    OffsetX = p.OffsetX,
                    OffsetY = p.OffsetY,
                    RotationDeg = p.RotationDeg,
                });
            }
            if (session is { Unplaced.Count: > 0 })
                _nest.Unplaced.AddRange(session.Unplaced);
        }

        if (string.IsNullOrWhiteSpace(doc.SessionJson))
            NcPreview.Text = doc.NcText ?? "";

        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        RestoreProjectStage(session?.Stage ?? (places.Count > 0 ? "nest" : "load"));
        ApplyStageVisibility();
        UpdateStageChrome();
        RefreshBridgeCount();
        RefreshOpsRail();
        RefreshCamFrames();
        RefreshExportFiles();
        if (session?.SelectedExportFile is { Length: > 0 } keepName)
        {
            var pick = _exportFiles.FirstOrDefault(f => f.FileName == keepName);
            if (pick is not null)
            {
                _syncingExportFiles = true;
                OutFileList.SelectedItem = pick;
                _syncingExportFiles = false;
                _exportSelected = pick;
                NcPreview.Text = pick.NcText;
            }
        }
        else if (_exportFiles.Count == 0 && !string.IsNullOrWhiteSpace(doc.NcText))
            NcPreview.Text = doc.NcText;
        UpdateNestSheetChrome();
        CanvasHost.InvalidateVisual();
        RememberRecentFile(projectPath, "project");
        MarkWorkSaved();
        SetStatus($"已打开工程 {doc.Name} · {_session.Package!.Panels.Count} 块板 · 摆位 {places.Count} · 刀路 {_opsOverlay.Count}", StatusKind.Success);
        ShowImportDialog(true, "打开工程", Path.GetFileName(projectPath), result,
            $"工程名: {doc.Name}\n机型: {doc.MachineId}\n已恢复摆位: {places.Count}\n刀路: {_opsOverlay.Count}\n桥: {_profileBridges.Count}");
    }

    /// <summary>Import result popup — success/fail + basic package stats.</summary>
    void ShowImportDialog(bool ok, string action, string sourceLabel, PackageImportResult? result, string? extra = null)
    {
        LogImportResult(ok, action, sourceLabel, result, extra);
        var sb = new StringBuilder();
        if (ok && _session.Package is { } pkg)
        {
            sb.AppendLine($"{action}成功");
            sb.AppendLine();
            sb.AppendLine($"文件: {sourceLabel}");
            sb.AppendLine($"格式: {pkg.SchemaName}  v{pkg.Version}");
            if (!string.IsNullOrEmpty(pkg.JobId))
                sb.AppendLine($"Job: {pkg.JobId}");
            sb.AppendLine($"单位: {pkg.Units}");
            sb.AppendLine($"板件: {pkg.Panels.Count}");
            sb.AppendLine($"板材规格: {pkg.Sheets.Count}");
            sb.AppendLine($"特征合计: {pkg.Panels.Sum(p => p.Features.Count)}");
            var mats = pkg.Panels.Select(p => p.Material).Where(m => !string.IsNullOrEmpty(m)).Distinct().Count();
            if (mats > 0) sb.AppendLine($"材料种类: {mats}");
            if (result is { Warnings.Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"警告 ({result.Warnings.Count}):");
                foreach (var w in result.Warnings.Take(6))
                    sb.AppendLine($"  · {w.Message}");
                if (result.Warnings.Count > 6)
                    sb.AppendLine($"  …另有 {result.Warnings.Count - 6} 条");
            }
            if (!string.IsNullOrWhiteSpace(extra))
            {
                sb.AppendLine();
                sb.AppendLine(extra.Trim());
            }
            MessageBox.Show(this, sb.ToString().TrimEnd(), $"{action}成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            sb.AppendLine($"{action}失败");
            sb.AppendLine();
            sb.AppendLine($"文件: {sourceLabel}");
            if (result is { Errors.Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("错误:");
                foreach (var err in result.Errors.Take(10))
                    sb.AppendLine($"  · [{err.Path}] {err.Message}");
                if (result.Errors.Count > 10)
                    sb.AppendLine($"  …另有 {result.Errors.Count - 10} 条");
            }
            if (result is { Warnings.Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine($"警告 ({result.Warnings.Count}):");
                foreach (var w in result.Warnings.Take(4))
                    sb.AppendLine($"  · {w.Message}");
            }
            if (!string.IsNullOrWhiteSpace(extra))
            {
                sb.AppendLine();
                sb.AppendLine(extra.Trim());
            }
            MessageBox.Show(this, sb.ToString().TrimEnd(), $"{action}失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void LogImportResult(bool ok, string action, string sourceLabel, PackageImportResult? result, string? extra)
    {
        var pkg = _session.Package;
        var summary = new PackageImportSummary
        {
            SchemaName = pkg?.SchemaName,
            Version = pkg?.Version.ToString(),
            JobId = pkg?.JobId,
            Units = pkg?.Units,
            PanelCount = pkg?.Panels.Count ?? 0,
            SheetCount = pkg?.Sheets.Count ?? 0,
            FeatureCount = pkg?.Panels.Sum(p => p.Features.Count) ?? 0,
            MaterialKinds = pkg?.Panels.Select(p => p.Material).Where(m => !string.IsNullOrEmpty(m)).Distinct().Count() ?? 0,
            ErrorCount = result?.Errors.Count ?? 0,
            WarningCount = result?.Warnings.Count ?? 0,
            Errors = (result?.Errors ?? []).Take(12).Select(e => $"[{e.Path}] {e.Message}").ToList(),
            Warnings = (result?.Warnings ?? []).Take(12).Select(w => w.Message).ToList(),
        };
        var payload = UsageLog.SummarizeImport(ok, sourceLabel, summary, extra);
        payload["uiAction"] = action;
        UsageLog.LogActionResult(
            "package.import",
            payload,
            error: ok ? null : string.Join("; ", summary.Errors.Take(3)));
    }

    void OnOpenUsageLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = UsageLog.AppDataLogDir();
            var latest = Path.Combine(dir, "app_usage_latest.json");
            UsageLog.LogEvent("ui", "desktop.openUsageLogs", new Dictionary<string, object?>
            {
                ["dir"] = dir,
                ["repoDir"] = UsageLog.RepoLogDir(),
                ["latestExists"] = File.Exists(latest),
            });
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
            SetStatus($"使用日志: {dir}");
        }
        catch (Exception ex)
        {
            SetStatus("无法打开日志目录: " + ex.Message);
            MessageBox.Show(this, ex.Message, "使用日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnSaveProjectClick(object sender, RoutedEventArgs e) => TrySaveProjectInteractive();

    /// <summary>Save dialog + write; false when the operator cancelled or nothing is loaded.</summary>
    bool TrySaveProjectInteractive()
    {
        if (_session.Package is null || string.IsNullOrWhiteSpace(_session.PackageJson))
        {
            SetStatus("请先载入方案再保存工程");
            return false;
        }

        var defaultName = ExportNaming.FileStem(_session.ResolvedProjectName) + ".db";
        var dlg = new SaveFileDialog
        {
            Filter = "OmniCam project|project.db;*.db|SQLite|*.db",
            FileName = defaultName,
            Title = "保存工程",
        };
        if (!string.IsNullOrEmpty(_session.ProjectDbPath))
            dlg.InitialDirectory = Path.GetDirectoryName(_session.ProjectDbPath);
        if (dlg.ShowDialog() != true) return false;

        var nestJson = _nest is { Ok: true }
            ? SqliteProjectStore.SerializeNest(_nest.Placements.Select(p => new NestPlacementDto
            {
                PanelId = p.PanelId,
                SheetIndex = p.SheetIndex,
                OffsetX = p.OffsetX,
                OffsetY = p.OffsetY,
                RotationDeg = p.RotationDeg,
            }))
            : null;

        var session = CaptureProjectSession();
        var name = string.IsNullOrWhiteSpace(_session.ProjectName)
            ? Path.GetFileNameWithoutExtension(dlg.FileName)
            : _session.ResolvedProjectName;
        _session.ProjectName = name;
        SyncProjectNameBox();
        _store.Save(dlg.FileName, new ProjectDocument
        {
            Name = name,
            PackageJson = _session.PackageJson!,
            SourceSnapshotJson = _session.SourceSnapshotJson,
            MachineId = SelectedMachineId(),
            NestPlacementsJson = nestJson,
            NcText = string.IsNullOrWhiteSpace(NcPreview.Text) || NcPreview.Text.StartsWith("//")
                ? null
                : NcPreview.Text,
            SessionJson = ProjectSessionCodec.Serialize(session),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _session.SetProjectDbPath(dlg.FileName);
        _session.MachineId = SelectedMachineId();
        _session.LabelerMachineId = SelectedLabelerMachineId();
        RememberRecentFile(dlg.FileName, "project");
        MarkWorkSaved();
        SetStatus($"已保存工程 → {dlg.FileName}");
        UsageLog.LogActionResult("project.save", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = dlg.FileName,
            ["panelCount"] = _session.Package.Panels.Count,
            ["hasNest"] = nestJson is not null,
            ["opCount"] = _opsOverlay.Count,
            ["bridgeCount"] = _profileBridges.Count,
            ["machineId"] = SelectedMachineId(),
            ["stage"] = _stage,
        });
        return true;
    }

    async void OnPingClick(object sender, RoutedEventArgs e) => await RefreshWorkerAsync(announce: true);

    void OnCamStrategyEnableClick(object sender, RoutedEventArgs e)
    {
        if (_syncingOpsStrategy) return;
        if (sender == RouteTongueChk || sender == RouteContourChk || sender == RouteGrooveChk || sender == RouteDrillChk)
        {
            _enableTongue = RouteTongueChk.IsChecked == true;
            _enableClearance = RouteGrooveChk.IsChecked == true;
            _enableDrilling = RouteDrillChk.IsChecked == true;
            var profileOn = RouteContourChk.IsChecked == true;
            _enableProfile = profileOn;
            _enableProfileLast = profileOn;
        }
        else
        {
            _enableTongue = OpsTongueChk.IsChecked == true;
            _enableClearance = OpsClearanceChk.IsChecked == true;
            _enableProfile = OpsProfileChk.IsChecked == true;
            _enableProfileLast = OpsProfileLastChk.IsChecked == true;
            _enableBridges = OpsBridgeChk.IsChecked == true;
            _enableDrilling = OpsDrillChk.IsChecked == true;
        }
        RebuildOpsOverlay();
        CanvasHost.InvalidateVisual();
        SetStatus($"刀路开关 · 半槽={_enableTongue} 清底={_enableClearance} 第一刀={_enableProfile} 最后一刀={_enableProfileLast}");
    }

    void OnPreflightClick(object sender, RoutedEventArgs e)
    {
        RebuildOpsOverlay();
        var report = RunPreflight();
        MessageBox.Show(this, NcPreflight.Format(report), report.Ok ? "预检通过" : "预检失败",
            MessageBoxButton.OK, report.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    bool GuardExportPreflight(IReadOnlyList<ExportNcFile>? files = null)
    {
        if (_session.ManufacturingDirty || _nest is not { Ok: true })
        {
            MessageBox.Show(this,
                "板件已编辑，或尚未完成有效密排。\n请重新密排并生成刀路后再导出。",
                "Nest/CAM 已失效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (_session.Package is not null)
        {
            var clearance = ParseMm(NestSpacingBox.Text, 12);
            var places = CurrentNestPlacements();
            if (files is { Count: > 0 })
            {
                var sheets = files.Select(f => f.SheetIndex).ToHashSet();
                places = places.Where(p => sheets.Contains(p.SheetIndex)).ToList();
            }
            var nestGate = NestExportGate.Check(
                _session.Package.Panels,
                places,
                clearance,
                allowAabbOverlap: UsesTrueShapeNest(),
                partInPartSlots: _partInPartSlots);
            if (!nestGate.Ok)
            {
                MessageBox.Show(this,
                    "密排间距/碰撞/混组硬门未通过，禁止导出：\n\n" +
                    string.Join("\n", nestGate.Errors.Take(20)),
                    "Nest 导出硬门",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        RebuildOpsOverlay();
        var report = RunPreflight(files, allSheets: files is null);
        RefreshPreflightMeta();
        if (report.Ok) return true;

        // Hard CAM safety errors cannot be overridden (pocket/tool gates).
        var hard = report.Issues.Where(i => i.Level == "error" && i.Code is
            "pocket_depth_missing" or "pocket_too_small_for_tool" or "missing_tool_id"
            or "groove_too_deep" or "depth_spoilboard" or "no_registration").ToList();
        if (hard.Count > 0)
        {
            MessageBox.Show(this,
                "预检硬错误，禁止导出：\n\n" + NcPreflight.Format(new PreflightReport { Ok = false, Issues = hard }),
                "预检未通过",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        var dlg = new OverrideReasonWindow(NcPreflight.Format(report)) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            SetStatus("已取消导出 · 预检未通过", StatusKind.Warning);
            return false;
        }
        UsageLog.LogActionResult("export.preflight.override", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["reason"] = dlg.Reason,
            ["issues"] = report.Issues.Select(i => i.Code).Distinct().ToArray(),
            ["files"] = files?.Select(f => f.FileName).ToArray(),
        });
        SetStatus("预检未通过，已记录原因并继续导出：" + dlg.Reason, StatusKind.Warning);
        return true;
    }

    int? CurrentDxfSheetIndex()
    {
        if (OutFileList?.SelectedItem is ExportNcFile picked)
            return picked.SheetIndex;
        if (_exportSelected is not null)
            return _exportSelected.SheetIndex;
        if (_nest is { Ok: true, Placements.Count: > 0 })
            return _activeNestSheet;
        return null;
    }

    IReadOnlyList<int> DxfSheetIndexesOfSelectedKind()
    {
        return ExportFilesOfSelectedKind()
            .Select(f => f.SheetIndex)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
    }

    string NestDxfFileName(int sheetIndex) =>
        $"{_session.Package?.JobId ?? "nest"}_S{sheetIndex + 1}.dxf";

    List<NestPlacement>? NestPlacementsForDxf()
    {
        if (_session.Package is null || _nest is not { Ok: true })
            return null;
        return CurrentNestPlacements();
    }

    void OnExportDxfClick(object sender, RoutedEventArgs e) => OnExportDxfSheetClick(sender, e);

    void OnExportDxfSheetClick(object sender, RoutedEventArgs e)
    {
        var sheet = CurrentDxfSheetIndex();
        if (sheet is null)
        {
            SetStatus("请先选中一张大板");
            return;
        }
        WriteExportDxfSheets([sheet.Value], oneFile: true);
    }

    void OnExportDxfKindClick(object sender, RoutedEventArgs e)
    {
        var sheets = DxfSheetIndexesOfSelectedKind();
        if (sheets.Count == 0)
        {
            SetStatus("请先选中一张该种类的大板");
            return;
        }
        WriteExportDxfSheets(sheets, oneFile: sheets.Count == 1);
    }

    void WriteExportDxfSheets(IReadOnlyList<int> sheetIndexes, bool oneFile)
    {
        var places = NestPlacementsForDxf();
        if (places is null || _session.Package is null)
        {
            SetStatus("无排版 — 先密排");
            return;
        }

        var files = _exportFiles.Where(f => sheetIndexes.Contains(f.SheetIndex)).ToList();
        if (files.Count > 0 && !GuardExportPreflight(files))
            return;
        if (files.Count == 0 && !GuardExportPreflight())
            return;

        if (oneFile)
        {
            var si = sheetIndexes[0];
            var dlg = new SaveFileDialog
            {
                Filter = "DXF (*.dxf)|*.dxf|All|*.*",
                FileName = NestDxfFileName(si),
                Title = "单独导出这张大板 DXF",
            };
            if (dlg.ShowDialog() != true) return;
            var dxf = NestDxfWriter.Write(_session.Package, places, si);
            File.WriteAllText(dlg.FileName, dxf);
            SetStatus($"已导出大板 {si + 1} DXF → {dlg.FileName}");
            UsageLog.LogActionResult("export.dxf.sheet", new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["path"] = dlg.FileName,
                ["sheetIndex"] = si,
                ["jobId"] = _session.Package.JobId,
            });
            return;
        }

        var folder = new OpenFolderDialog { Title = "选择 DXF 导出目录" };
        if (folder.ShowDialog() != true) return;
        var dir = folder.FolderName;
        foreach (var si in sheetIndexes)
        {
            var dxf = NestDxfWriter.Write(_session.Package, places, si);
            File.WriteAllText(Path.Combine(dir, NestDxfFileName(si)), dxf);
        }
        SetStatus($"已按种类导出 {sheetIndexes.Count} 张大板 DXF → {dir}");
        UsageLog.LogActionResult("export.dxf.kind", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["count"] = sheetIndexes.Count,
            ["dir"] = dir,
            ["sheets"] = sheetIndexes.Select(i => i + 1).ToArray(),
            ["jobId"] = _session.Package.JobId,
        });
    }

    void OnExportJobSheetClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("无方案");
            return;
        }
        var places = _nest?.Placements.Select(p => new NestPlacement
        {
            PanelId = p.PanelId,
            SheetIndex = p.SheetIndex,
            OffsetX = p.OffsetX,
            OffsetY = p.OffsetY,
            RotationDeg = p.RotationDeg,
        }).ToList();
        var util = EstimateUtilization();
        var html = JobSheetBuilder.BuildHtml(
            _session.Package,
            ActiveProfileForCam(),
            places,
            _locked,
            NcPreflight.Format(RunPreflight()),
            util,
            _nest?.Unplaced.Count ?? 0);
        var dlg = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html|All|*.*",
            FileName = $"{_session.Package.JobId ?? "job"}_sheet.html",
            Title = "导出工单",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, html);
        SetStatus($"已导出工单 → {dlg.FileName}");
        UsageLog.LogActionResult("export.jobSheet", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = dlg.FileName,
            ["jobId"] = _session.Package.JobId,
            ["placementCount"] = places?.Count ?? 0,
        });
    }

    void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || string.IsNullOrWhiteSpace(_session.PackageJson))
        {
            SetStatus("无包可导出");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Cut package JSON (*.json)|*.json|All|*.*",
            FileName = $"{_session.Package.JobId ?? "package"}.cut.json",
            Title = "导出 cut-package JSON",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, _session.PackageJson);
        SetStatus($"已导出 JSON → {dlg.FileName}");
        UsageLog.LogActionResult("export.cutJson", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = dlg.FileName,
            ["jobId"] = _session.Package.JobId,
            ["panelCount"] = _session.Package.Panels.Count,
        });
    }

    void OnExportBundleClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true })
        {
            SetStatus("需要方案 + 密排");
            return;
        }
        if (!GuardExportPreflight()) return;
        if (!HasNcText())
        {
            SetStatus("无 NC — 先生成加工档");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Folder marker|*.txt",
            FileName = "export_here.txt",
            Title = "选择导出目录（保存此标记文件所在文件夹）",
        };
        if (dlg.ShowDialog() != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName)!;
        var baseName = _session.Package.JobId ?? "job";
        var places = _nest.Placements.Select(p => new NestPlacement
        {
            PanelId = p.PanelId,
            SheetIndex = p.SheetIndex,
            OffsetX = p.OffsetX,
            OffsetY = p.OffsetY,
            RotationDeg = p.RotationDeg,
        }).ToList();
        RebuildOpsOverlay();
        var profile = ActiveProfileForCam();
        var html = JobSheetBuilder.BuildHtml(
            _session.Package, profile, places, _locked,
            NcPreflight.Format(RunPreflight()), EstimateUtilization(), _nest.Unplaced.Count);
        var bundle = SheetBundleBuilder.Build(
            _session.Package,
            places,
            _opsOverlay,
            profile,
            jobSheetHtml: html,
            recipe: CurrentPostRecipe());
        var written = SheetBundleBuilder.WriteToDirectory(bundle, dir);
        if (!string.IsNullOrWhiteSpace(_session.PackageJson))
            File.WriteAllText(Path.Combine(dir, bundle.JobId + ".cut.json"), _session.PackageJson);
        try { File.Delete(dlg.FileName); } catch { /* marker optional */ }
        SetStatus($"一键打包完成 · sheets={bundle.Sheets.Count} programs={bundle.Sheets.Sum(s => s.ToolPrograms.Count)} → {dir}");
        UsageLog.LogActionResult("export.bundle", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["dir"] = dir,
            ["jobId"] = bundle.JobId,
            ["postId"] = bundle.PostId,
            ["sheetCount"] = bundle.Sheets.Count,
            ["fileCount"] = written.Count,
            ["programCount"] = bundle.Sheets.Sum(s => s.ToolPrograms.Count),
        });
        MessageBox.Show(this,
            $"已写入 {written.Count} 个文件（每 Sheet×Tool 独立 NC；每 Sheet 一份 DXF/manifest）\nPost={bundle.PostId}\n\n目录:\n{dir}",
            "一键打包成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    double? EstimateUtilization()
    {
        if (_session.Package is null || _nest is not { Ok: true }) return null;
        var sw = ParseMm(StockWidthBox.Text, 1200);
        var sh = ParseMm(StockLengthBox.Text, 2400);
        double used = 0;
        var placed = _nest.Placements.Select(p => p.PanelId).ToHashSet();
        foreach (var p in _session.Package.Panels.Where(p => placed.Contains(p.PanelId)))
        {
            var (w, h) = SizeOf(p);
            used += w * h;
        }
        var sheetArea = sw * sh * Math.Max(1, _nest.SheetCount);
        return sheetArea > 0 ? used / sheetArea * 100 : null;
    }

    void OnSaveNcClick(object sender, RoutedEventArgs e)
    {
        if (!GuardExportPreflight()) return;
        var text = NcPreview.Text;
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("//"))
        {
            SetStatus("没有可保存的 NC — 请先完成密排并计算刀路", StatusKind.Warning);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "NC (*.nc)|*.nc|G-code (*.ngc)|*.ngc|All|*.*",
            FileName = $"{SelectedMachineId()}.nc",
            Title = "保存 NC",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, text);
        var saveDir = Path.GetDirectoryName(dlg.FileName)!;
        var labels = _exportSelected is { Labels.Count: > 0 } sel
            ? WriteLabelBmps(saveDir, [sel])
            : (Text: null, Missing: 0);
        SetStatus(labels.Text is null
            ? $"已保存 NC → {dlg.FileName}"
            : $"已保存 NC · {labels.Text}");
        AnnounceExport(1, _exportSelected?.Labels.Count ?? 0, labels.Missing, saveDir);
        UsageLog.LogActionResult("export.nc", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["path"] = dlg.FileName,
            ["machineId"] = SelectedMachineId(),
            ["lineCount"] = text.Split('\n').Length,
            ["jobId"] = _session.Package?.JobId,
        });
        _stageChanging = true;
        StageTabs.SelectedIndex = 4;
        _stage = "out";
        _stageChanging = false;
        ApplyStageVisibility();
        UpdateStageChrome();
        RefreshWorkflowDots();
    }

    async void OnOneClickExportClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }
        if (_nest is not { Ok: true })
        {
            SetStatus("一键导出：正在密排…");
            await RunNestAsync(withNc: false);
        }
        if (_nest is { Ok: true } && !HasNcText())
        {
            SetStatus("一键导出：正在按规则计算刀路…");
            RebuildOpsOverlay();
        }
        if (!HasNcText())
        {
            SetStatus("一键导出失败：无 NC");
            return;
        }
        OnSaveNcClick(sender, e);
    }

    static (double w, double h) SizeOf(PanelPart p)
    {
        var pts = p.Outline.Points;
        if (pts.Count < 2) return (0, 0);
        return (pts.Max(pt => pt.X) - pts.Min(pt => pt.X), pts.Max(pt => pt.Y) - pts.Min(pt => pt.Y));
    }

    /// <summary>
    /// Nest/CAM run in-process, so a missing Worker is not an operator problem: the badge
    /// stays neutral and the failure detail lives in the tooltip. Only an explicit Ping
    /// (更多 → 计算引擎自检) reports into the status line.
    /// </summary>
    async Task RefreshWorkerAsync(bool announce = false)
    {
        var ok = await _worker.EnsureStartedAsync();
        if (!ok)
        {
            WorkerBadge.Text = "计算引擎 · 本地";
            WorkerBadge.Foreground = (Brush)FindResource("TextMutedBrush");
            WorkerBadge.ToolTip = "排版与刀路在本机计算。独立 Worker 进程未运行：" + (_worker.LastError ?? "未知原因");
            if (announce)
                SetStatus("计算引擎自检：本机计算可用，独立 Worker 未运行（" + (_worker.LastError ?? "未知原因") + "）", StatusKind.Warning);
            return;
        }

        try
        {
            var client = _worker.GetHealthClient()!;
            var ver = await client.GetWorkerVersionAsync(new());
            var ping = await client.PingAsync(new() { Token = "ui" });
            WorkerBadge.Text = $"计算引擎 · Worker {ver.WorkerVersion}";
            WorkerBadge.Foreground = (Brush)FindResource("SuccessBrush");
            WorkerBadge.ToolTip = $"独立 Worker 进程在线 · contract={ver.ContractVersion} · {ping.Message}";
            if (announce)
                SetStatus($"计算引擎自检通过 · Worker {ver.WorkerVersion} · contract={ver.ContractVersion} · 机型 {SelectedMachineId()}", StatusKind.Success);
        }
        catch (Exception ex)
        {
            WorkerBadge.Text = "计算引擎 · 本地";
            WorkerBadge.Foreground = (Brush)FindResource("WarningBrush");
            WorkerBadge.ToolTip = "独立 Worker 进程无响应：" + ex.Message;
            if (announce)
                SetStatus("计算引擎自检：Worker 无响应，继续使用本机计算（" + ex.Message + "）", StatusKind.Warning);
        }
    }

    void OnPartSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = PartList.SelectedItem as PanelPart
            ?? (PartList.SelectedItem as StockPartRow)?.Representative;
        if (!_syncingNestSelection)
        {
            _nestSelected.Clear();
            if (_selected is not null)
                _nestSelected.Add(_selected.PanelId);
        }
        if (_selected is not null && _locked.Contains(_selected.PanelId))
            LockPlaceBtn.Content = "解锁摆位";
        else
            LockPlaceBtn.Content = "锁定摆位";
        if (!_syncingNestSelection && _selected is not null && _nest is { Ok: true })
        {
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == _selected.PanelId);
            if (place is not null && place.SheetIndex != _activeNestSheet)
            {
                _activeNestSheet = place.SheetIndex;
                UpdateNestSheetChrome();
            }
        }
        RefreshGeomRail();
        CanvasHost.InvalidateVisual();
    }

    static bool IsNestChromeClick(object? source)
    {
        if (source is not DependencyObject d) return false;
        while (d is not null)
        {
            if (d is FrameworkElement { Name: "NestCanvasChrome" or "NestSheetPrevBtn" or "NestSheetNextBtn" or "NestCreatePanelBtn" or "OutSimChrome" or "ViewportTools" or "ToastHost" })
                return true;
            if (d is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    (float X, float Y) CanvasPixelPos(MouseEventArgs e)
    {
        // 榧犳爣鏄?DIP锛汼kia PaintSurface 鏄墿鐞嗗儚绱?鈥?蹇呴』鍚屼竴鍧愭爣绯诲仛 hit-test
        var pos = e.GetPosition(CanvasHost);
        _lastCanvasX = (float)(pos.X * _dpiX);
        _lastCanvasY = (float)(pos.Y * _dpiY);
        return (_lastCanvasX, _lastCanvasY);
    }

    void RefreshDpi()
    {
        var dpi = VisualTreeHelper.GetDpi(CanvasHost);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;
    }

    void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        // Preview on CanvasPane would steal ◀ ▶ / chrome clicks for box-select.
        if (IsNestChromeClick(e.OriginalSource))
            return;

        RefreshDpi();
        var (x, y) = CanvasPixelPos(e);

        if (_stage == "load" && _selected is not null)
        {
            var w = _surfaceW > 0 ? _surfaceW : Math.Max(1, (int)(CanvasHost.ActualWidth * _dpiX));
            var h = _surfaceH > 0 ? _surfaceH : Math.Max(1, (int)(CanvasHost.ActualHeight * _dpiY));
            var view = GeomInteraction.BuildView(_selected, w, h);
            _geomView = view;
            var hit = GeomInteraction.HitTest(_selected, view, x, y);
            if (hit is null || hit.Value.Type == "panel")
            {
                _dragMode = null;
                SetStatus("几何: 点蓝色孔心 / 红色槽端 / 黑边手柄再拖");
                return;
            }
            _dragMode = "geom";
            _geomHit = hit;
            _geomStart = _selected;
            CanvasPane.CaptureMouse();
            SetStatus($"拖动 {hit.Value.Type}");
            e.Handled = true;
            return;
        }

        if ((_stage is "nest" or "ops") && _nest is { Ok: true } && _session.Package is not null)
        {
            EnsureNestViewMetrics();
            // Holding bay first (right rail cards).
            var holdId = HitTestHolding(x, y);
            if (holdId is not null)
            {
                var hitPanel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == holdId);
                if (hitPanel is not null)
                    PartList.SelectedItem = hitPanel;
                if (_stage == "ops")
                {
                    SetStatus($"待用 · {holdId}");
                    return;
                }
                var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                if (additive)
                {
                    foreach (var id in _nestSelected
                                 .Where(sid => _nestHolding.All(h => h.PanelId != sid))
                                 .ToList())
                        _nestSelected.Remove(id);
                    if (!_nestSelected.Add(holdId))
                        _nestSelected.Remove(holdId);
                    SyncPartListFromNestSelection(holdId);
                    SetStatus(_nestSelected.Count == 0
                        ? "已取消选中"
                        : $"待用已选 {_nestSelected.Count} 件（Ctrl 加减 · 拖进密排）");
                    CanvasHost.InvalidateVisual();
                    e.Handled = true;
                    return;
                }
                var holdIds = HoldingSelectedIds(holdId);
                _nestSelected.Clear();
                foreach (var id in holdIds)
                    _nestSelected.Add(id);
                SyncPartListFromNestSelection(holdId);
                _dragMode = "nest";
                _nestDragFromHold = true;
                _nestDragPanelId = holdId;
                _holdSlideHasValid = false;
                var (mx, my) = ScreenToSheet(x, y);
                _nestStartMx = mx;
                _nestStartMy = my;
                _nestOrigOx = 0;
                _nestOrigOy = 0;
                _nestDragRotDeg = _nestHolding.FirstOrDefault(h => h.PanelId == holdId)?.RotationDeg ?? 0;
                CanvasPane.CaptureMouse();
                SetStatus(holdIds.Count > 1
                    ? $"从待用区拖回 {holdIds.Count} 件 · 拖动中右键转90° · Alt 上下左右 · S 硬约束"
                    : $"从待用区拖回 · {holdId} · 拖动中右键转90° · Alt 上下左右 · S 硬约束");
                e.Handled = true;
                return;
            }

            if (_stage == "ops" && _bridgeDeleteMode)
            {
                TryHandleBridgeDeleteClick(x, y);
                e.Handled = true;
                return;
            }

            if (_stage == "ops" && _bridgeManualMode)
            {
                TryHandleBridgeManualClick(x, y);
                e.Handled = true;
                return;
            }

            if (_stage == "nest")
            {
                var labelId = HitTestLabel(x, y);
                if (labelId is not null)
                {
                    var labelPanel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == labelId);
                    _nestSelected.Clear();
                    _nestSelected.Add(labelId);
                    SyncPartListFromNestSelection(labelId);
                    _dragMode = "label";
                    _nestDragFromHold = false;
                    _nestDragPanelId = labelId;
                    CanvasPane.CaptureMouse();
                    var labelPlace = _nest.Placements.FirstOrDefault(p => p.PanelId == labelId);
                    if (labelPanel is not null && labelPlace is not null)
                    {
                        var a = ResolveLabelAnchor(labelPanel, labelPlace.RotationDeg);
                        SetStatus($"拖贴标 · {labelPanel.DisplayPartName} · {a.LocalX:0.#},{a.LocalY:0.#}");
                    }
                    else
                        SetStatus($"拖贴标 · {labelId}");
                    e.Handled = true;
                    return;
                }
            }

            var hitId = HitTestNest(x, y);
            if (hitId is null)
            {
                if (_stage == "ops" || ScreenInHoldingBay(x))
                {
                    SetStatus(ScreenInHoldingBay(x) ? "待用区 · 从大板拖入板件" : "未点中板件");
                    return;
                }
                var (bx, by) = ScreenToSheet(x, y);
                _dragMode = "nestBox";
                _nestBoxX0 = _nestBoxX1 = bx;
                _nestBoxY0 = _nestBoxY1 = by;
                CanvasPane.CaptureMouse();
                SetStatus("框选 · 右划全包围 · 左划碰到即选");
                e.Handled = true;
                return;
            }
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == hitId);
            if (place is null) return;
            var nestPanel = _session.Package?.Panels.FirstOrDefault(p => p.PanelId == hitId);
            var nestAdditive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            if (nestAdditive)
            {
                if (!_nestSelected.Add(hitId))
                    _nestSelected.Remove(hitId);
                SyncPartListFromNestSelection(hitId);
                SetStatus(_nestSelected.Count == 0
                    ? "已取消选中"
                    : _nestSelected.Count == 2
                        ? "已选 2 件（Ctrl 加减 · 按住 D 看间距）"
                        : $"已选 {_nestSelected.Count} 件（Ctrl 加减）");
                CanvasHost.InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (_stage == "ops")
            {
                _nestSelected.Clear();
                _nestSelected.Add(hitId);
                SyncPartListFromNestSelection(hitId);
                SetStatus($"选中 {hitId}");
                return;
            }
            if (_locked.Contains(hitId))
            {
                SetStatus($"已锁定 · {hitId}");
                return;
            }
            if (!_nestSelected.Contains(hitId))
            {
                _nestSelected.Clear();
                _nestSelected.Add(hitId);
            }
            SyncPartListFromNestSelection(hitId);
            var (smx, smy) = ScreenToSheet(x, y);
            _dragMode = "nest";
            _nestDragFromHold = false;
            _nestDragPanelId = hitId;
            _nestStartMx = smx;
            _nestStartMy = smy;
            _nestOrigOx = place.OffsetX;
            _nestOrigOy = place.OffsetY;
            _nestDragRotDeg = place.RotationDeg;
            CaptureNestGroupOrig();
            CanvasPane.CaptureMouse();
            SetStatus(NestDragStatus(_nestSelected.Count > 1 ? _nestSelected.Count : 0, hitId));
            e.Handled = true;
        }
    }

    void OnCanvasMove(object sender, MouseEventArgs e)
    {
        var (x, y) = CanvasPixelPos(e);
        UpdateViewportReadout(x, y);

        if (_simPanning)
        {
            CommitSimView(_simUserScale, _simPanOrigX + (x - _simPanStartX), _simPanOrigY + (y - _simPanStartY));
            CanvasHost.InvalidateVisual();
            e.Handled = true;
            return;
        }

        // hover cursor when not dragging
        if (_dragMode is null)
        {
            UpdateHoverCursor(x, y);
            if (e.LeftButton != MouseButtonState.Pressed) return;
        }

        if (_dragMode is null || e.LeftButton != MouseButtonState.Pressed) return;

        if (_dragMode == "geom" && _geomHit is not null && _geomStart is not null && _geomView is not null)
        {
            var (lx, ly) = GeomInteraction.ToLocal(_geomView.Value, x, y);
            _selected = GeomInteraction.ApplyDrag(_geomStart, _geomHit.Value, lx, ly);
            CanvasHost.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragMode == "nestBox")
        {
            var (mx, my) = ScreenToSheet(x, y);
            _nestBoxX1 = mx;
            _nestBoxY1 = my;
            SetStatus(NestDrag.IsCrossingSelect(_nestBoxX0, _nestBoxX1)
                ? "交叉选择（碰到即选）"
                : "窗口选择（全包围）");
            CanvasHost.InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragMode == "label" && _nestDragPanelId is not null)
        {
            ApplyLabelDragFromScreen(x, y);
            e.Handled = true;
            return;
        }

        if (_dragMode == "nest" && _nestDragPanelId is not null && _nest is { Ok: true })
        {
            ApplyNestDragFromScreen(x, y);
            e.Handled = true;
        }
    }

    void ApplyNestDragAtLastPointer() =>
        ApplyNestDragFromScreen(_lastCanvasX, _lastCanvasY);

    void ApplyNestDragFromScreen(float x, float y)
    {
        if (_dragMode != "nest" || _nestDragPanelId is null || _nest is not { Ok: true })
            return;
        if (_nestDragFromHold)
        {
            UpdateHoldPreviewFromScreen(x, y);
            CanvasHost.InvalidateVisual();
            return;
        }

        var (mx, my) = ScreenToSheet(x, y);
        var altLock = AltIsDown();
        var hard = SIsDown() && !IsTypingTarget();
        var (ox, oy) = NestDrag.DragOffset(
            _nestOrigOx, _nestOrigOy, _nestStartMx, _nestStartMy, mx, my, altLock);
        var place = _nest.Placements.FirstOrDefault(p => p.PanelId == _nestDragPanelId);
        if (place is null || _session.Package is null) return;
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        if (!byId.TryGetValue(_nestDragPanelId, out var panel))
            return;
        var (sw, sh, _) = ActiveSheetMetrics();
        var inset = ActiveSheetInsets();
        var towardBay = ScreenInHoldingBay(x);
        place.RotationDeg = _nestDragRotDeg;
        if (towardBay)
        {
            place.OffsetX = ox;
            place.OffsetY = oy;
        }
        else
        {
            var (cx, cy) = NestDrag.ClampOnSheet(
                panel, ox, oy, place.RotationDeg, sw, sh, inset);
            ox = cx;
            oy = cy;
            (ox, oy) = ClampPipChild(_nestDragPanelId, panel, ox, oy, place.RotationDeg);
            if (hard)
            {
                var spacing = ParseMm(NestSpacingBox.Text, 12);
                var movingIds = _nestGroupOrig.Count > 1
                    ? _nestGroupOrig.Keys.ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal) { _nestDragPanelId };
                var members = BuildSlideMembers(byId, movingIds, _nestDragPanelId);
                var others = _nest.Placements
                    .Where(p => !movingIds.Contains(p.PanelId))
                    .Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
                    .ToList();
                    (ox, oy) = NestDrag.SlideTo(
                    members, _nestDragPanelId,
                    place.OffsetX, place.OffsetY, ox, oy,
                    _activeNestSheet, others, byId, sw, sh, spacing, inset,
                    _nestOrigOx, _nestOrigOy,
                    PipIgnorePairs());
                (ox, oy) = ClampPipChild(_nestDragPanelId, panel, ox, oy, place.RotationDeg);
            }
            place.OffsetX = ox;
            place.OffsetY = oy;
        }
        if (_nestGroupOrig.Count > 1)
        {
            var dx = place.OffsetX - _nestOrigOx;
            var dy = place.OffsetY - _nestOrigOy;
            foreach (var (id, orig) in _nestGroupOrig)
            {
                if (id == _nestDragPanelId) continue;
                var other = _nest.Placements.FirstOrDefault(p => p.PanelId == id);
                if (other is null) continue;
                if (!byId.TryGetValue(id, out var otherPanel))
                    continue;
                var nx = orig.Ox + dx;
                var ny = orig.Oy + dy;
                if (!towardBay && !hard)
                    (nx, ny) = NestDrag.ClampOnSheet(
                        otherPanel, nx, ny, other.RotationDeg, sw, sh, inset);
                other.OffsetX = nx;
                other.OffsetY = ny;
            }
        }
        CanvasHost.InvalidateVisual();
        if (hard || altLock)
            SetStatus(NestDragStatus(_nestGroupOrig.Count > 1 ? _nestGroupOrig.Count : 0, _nestDragPanelId));
    }

    void UpdateHoldPreviewFromScreen(float x, float y)
    {
        _holdPreviewOnSheet = false;
        _holdPreviewBlocked = false;
        _holdPreviewPlaces.Clear();
        if (_nestDragPanelId is null || _session.Package is null || _nest is not { Ok: true })
            return;
        var dragIds = HoldingDragIds();
        if (ScreenInHoldingBay(x))
        {
            SetStatus(dragIds.Count > 1
                ? $"从待用区拖回 {dragIds.Count} 件 · 拖到左侧大板"
                : $"从待用区拖回 · {_nestDragPanelId} · 拖到左侧大板");
            return;
        }

        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        var sheetKey = ActiveSheetGroupKey();
        var packing = new List<(string Id, double W, double H, double Rot)>();
        var materialOk = true;
        foreach (var id in dragIds)
        {
            if (!byId.TryGetValue(id, out var panel)) continue;
            var held = _nestHolding.FirstOrDefault(h => h.PanelId == id);
            var rot = held?.RotationDeg ?? 0;
            var (w, h) = NestDrag.SizeRotated(panel, rot);
            packing.Add((id, w, h, rot));
            var partKey = held is not null
                ? NestGroupKey.From(held.Material, held.ThicknessMm)
                : NestGroupKey.From(panel.Material, panel.ThicknessMm);
            if (!SameNestMaterial(partKey, sheetKey))
                materialOk = false;
        }
        if (packing.Count == 0) return;

        var (sw, sh, _) = ActiveSheetMetrics();
        var inset = ActiveSheetInsets();
        var spacing = ActiveSheetSpacingMm();
        var maxW = Math.Max(1, sw - inset.Left - inset.Right);
        var (groupW, groupH, packed) = NestDrag.PackHoldCluster(packing, spacing, maxW);

        var (mx, my) = ScreenToSheet(x, y);
        var altLock = AltIsDown();
        var hard = SIsDown() && !IsTypingTarget();
        if (altLock)
        {
            var (cdx, cdy) = NestDrag.CardinalDelta(mx - _nestStartMx, my - _nestStartMy);
            mx = _nestStartMx + cdx;
            my = _nestStartMy + cdy;
        }
        var rawOx = NestDrag.SnapMm(mx - groupW * 0.5, 1);
        var rawOy = NestDrag.SnapMm(my - groupH * 0.5, 1);
        var (groupOx, groupOy) = NestDrag.ClampGroupOnSheet(
            groupW, groupH, rawOx, rawOy, sw, sh, inset);

        var allowOverlap = AllowOverlapChk.IsChecked == true;
        var others = _nest.Placements
            .Where(p => p.SheetIndex == _activeNestSheet)
            .Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
            .ToList();
        var blocked = !materialOk;
        if (groupW > sw - inset.Left - inset.Right + 1e-6 || groupH > sh - inset.Bottom - inset.Top + 1e-6)
            blocked = true;

        var grabPacked = packed.FirstOrDefault(p => p.Id == _nestDragPanelId);
        if (grabPacked.Id is null && packed.Count > 0) grabPacked = packed[0];
        var members = new List<NestDrag.SlideMember>();
        if (grabPacked.Id is not null)
        {
            foreach (var p in packed)
            {
                if (!byId.TryGetValue(p.Id, out var pan)) continue;
                members.Add(new NestDrag.SlideMember(
                    p.Id, pan,
                    p.LocalOx - grabPacked.LocalOx,
                    p.LocalOy - grabPacked.LocalOy,
                    p.Rot));
            }
        }

        if (hard && members.Count > 0 && grabPacked.Id is not null && !blocked)
        {
            var desiredOx = groupOx + grabPacked.LocalOx;
            var desiredOy = groupOy + grabPacked.LocalOy;
            var fromOx = _holdSlideHasValid ? _holdSlideOx : desiredOx;
            var fromOy = _holdSlideHasValid ? _holdSlideOy : desiredOy;
            var safeOx = _holdSlideHasValid ? _holdSlideOx : desiredOx;
            var safeOy = _holdSlideHasValid ? _holdSlideOy : desiredOy;
            var (sx, sy) = NestDrag.SlideTo(
                members, grabPacked.Id, fromOx, fromOy, desiredOx, desiredOy,
                _activeNestSheet, others, byId, sw, sh, spacing, inset, safeOx, safeOy);
            if (NestDrag.PoseFits(
                    members, grabPacked.Id, sx, sy, _activeNestSheet, others, byId,
                    sw, sh, spacing, inset))
            {
                groupOx = sx - grabPacked.LocalOx;
                groupOy = sy - grabPacked.LocalOy;
                _holdSlideOx = sx;
                _holdSlideOy = sy;
                _holdSlideHasValid = true;
            }
            else if (_holdSlideHasValid)
            {
                groupOx = _holdSlideOx - grabPacked.LocalOx;
                groupOy = _holdSlideOy - grabPacked.LocalOy;
            }
        }

        foreach (var part in packed)
        {
            var ox = groupOx + part.LocalOx;
            var oy = groupOy + part.LocalOy;
            _holdPreviewPlaces.Add(new CanvasPainter.HoldPreviewPart(part.Id, ox, oy, part.Rot));
            if (blocked || allowOverlap || hard) continue;
            if (!byId.TryGetValue(part.Id, out var panel)) continue;
            var (_, _, hit) = NestDrag.Resolve(
                panel, part.Id, ox, oy, part.Rot, _activeNestSheet,
                others, byId, sw, sh, spacing, inset,
                (ox, oy), allowOverlap: false, PipIgnorePairs(), UsesTrueShapeNest());
            if (hit) blocked = true;
        }

        if (hard && _holdSlideHasValid)
            blocked = false;

        _holdPreviewOnSheet = true;
        _holdPreviewBlocked = blocked;
        if (!materialOk)
            SetStatus($"材料不符 · 当前大板是 {sheetKey}");
        else if (blocked)
            SetStatus($"投影重叠或间距不足 · {packing.Count} 件 · 拖动中右键转90°");
        else
            SetStatus($"投影 {packing.Count} 件 · 拖动中右键转90° · Alt 上下左右 · S 硬约束 · 松开放入");
    }

    List<string> HoldingSelectedIds(string grabbedId)
    {
        var inHold = _nestHolding.Select(h => h.PanelId).ToHashSet(StringComparer.Ordinal);
        var selectedHold = _nestSelected.Where(inHold.Contains).ToHashSet(StringComparer.Ordinal);
        if (!selectedHold.Contains(grabbedId))
            return [grabbedId];
        return _nestHolding.Where(h => selectedHold.Contains(h.PanelId)).Select(h => h.PanelId).ToList();
    }

    List<string> HoldingDragIds()
    {
        var grabbed = _nestDragPanelId;
        if (grabbed is null) return [];
        return HoldingSelectedIds(grabbed);
    }

    bool _nestRightHandled;

    void OnWindowRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_nestRightHandled || _stage != "nest") return;
        if (IsNestChromeClick(e.OriginalSource)) return;
        CanvasPixelPos(e);
        if (_dragMode == "label")
            return;
        var leftHeld = Mouse.LeftButton == MouseButtonState.Pressed || _dragMode == "nest";
        if (leftHeld && _dragMode == "nest" && _nestDragPanelId is not null)
        {
            RotateNestDragClockwise90();
            _nestRightHandled = true;
            e.Handled = true;
            return;
        }
        if (_dragMode is not null) return;
        if (leftHeld) return;
        if (_nest is not { Ok: true }) return;
        EnsureNestViewMetrics();
        var holdId = HitTestHolding(_lastCanvasX, _lastCanvasY);
        if (holdId is not null)
        {
            if (!_nestSelected.Contains(holdId))
            {
                _nestSelected.Clear();
                _nestSelected.Add(holdId);
                SyncPartListFromNestSelection(holdId);
                CanvasHost.InvalidateVisual();
            }
            _nestContextPanelId = holdId;
            _nestContextInHold = true;
            ShowNestHoldContextMenu();
            _nestRightHandled = true;
            e.Handled = true;
            return;
        }

        var hitId = HitTestNest(_lastCanvasX, _lastCanvasY);
        if (hitId is null) return;
        if (!_nestSelected.Contains(hitId))
        {
            _nestSelected.Clear();
            _nestSelected.Add(hitId);
            SyncPartListFromNestSelection(hitId);
            CanvasHost.InvalidateVisual();
        }
        _nestContextPanelId = hitId;
        _nestContextInHold = false;
        ShowNestPartContextMenu();
        _nestRightHandled = true;
        e.Handled = true;
    }

    void OnWindowRightUp(object sender, MouseButtonEventArgs e)
    {
        _nestRightHandled = false;
        if (_dragMode == "nest" || _stage == "nest")
            e.Handled = true;
    }

    void OnCanvasRightDown(object sender, MouseButtonEventArgs e) => OnWindowRightDown(sender, e);

    void OnCanvasRightUp(object sender, MouseButtonEventArgs e) => OnWindowRightUp(sender, e);

    void ShowNestPartContextMenu()
    {
        if (TryFindResource("NestPartContextMenu") is not ContextMenu menu)
            return;
        menu.PlacementTarget = CanvasPane;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    void ShowNestHoldContextMenu()
    {
        if (TryFindResource("NestHoldContextMenu") is not ContextMenu menu)
            return;
        menu.PlacementTarget = CanvasPane;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    List<string> NestContextIds()
    {
        var hit = _nestContextPanelId;
        if (string.IsNullOrWhiteSpace(hit) || _session.Package is null)
            return [];
        var zone = _nestContextInHold
            ? _nestHolding.Select(h => h.PanelId).ToHashSet(StringComparer.Ordinal)
            : (_nest?.Placements.Select(p => p.PanelId).ToHashSet(StringComparer.Ordinal)
               ?? new HashSet<string>(StringComparer.Ordinal));
        if (_nestSelected.Contains(hit) && _nestSelected.Count > 1)
            return _nestSelected.Where(zone.Contains).ToList();
        return zone.Contains(hit) ? [hit] : [hit];
    }

    static string RotateGrain90(PanelPart panel)
    {
        var g = GrainAlign.NormalizePart(panel.GrainDirection ?? panel.Orientation?.GrainDirection);
        return g == "X" ? "Y" : "X";
    }

    void OnNestRotateGrainClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true }) return;
        var ids = NestContextIds();
        if (ids.Count == 0) return;

        var moved = 0;
        foreach (var id in ids)
        {
            var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == id);
            if (panel is null) continue;
            var next = panel.WithGrain(RotateGrain90(panel));
            _session.ReplacePanel(next);
            if (!_nestContextInHold)
            {
                if (MovePlacementToHolding(id, next, refresh: false))
                    moved++;
            }
            else
            {
                var held = _nestHolding.FirstOrDefault(h => h.PanelId == id);
                ParkInHolding(next, held?.RotationDeg ?? 0);
            }
        }

        if (_selected is not null)
            _selected = _session.Package.Panels.FirstOrDefault(p => p.PanelId == _selected.PanelId) ?? _selected;

        RefreshNestUiKeepSheet(_selected?.PanelId, _activeNestSheet);
        RefreshStockMaterialCards();
        SetStatus(_nestContextInHold
            ? $"已转纹路 90° · {ids.Count} 件 · 仍在待用区"
            : $"已转纹路 90° · {ids.Count} 件 · {moved} 件已入待用区");
    }

    void OnNestHoldCopyClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true }) return;
        var ids = NestContextIds();
        if (ids.Count == 0) return;

        string? lastId = null;
        foreach (var id in ids)
        {
            var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == id);
            if (panel is null) continue;
            var copyId = _session.NextCopyPanelId(id);
            var copy = PanelEdit.Duplicate(panel, copyId);
            if (!GrainAlign.HasPartGrain(copy)
                && KindHasGrain(copy.Material, copy.ThicknessMm))
                copy = copy.WithGrain("X");
            _session.ReplacePanel(copy);
            var held = _nestHolding.FirstOrDefault(h => h.PanelId == id);
            ParkInHolding(copy, held?.RotationDeg ?? 0);
            lastId = copyId;
        }

        if (lastId is not null)
        {
            _nestSelected.Clear();
            _nestSelected.Add(lastId);
            _selected = _session.Package.Panels.FirstOrDefault(p => p.PanelId == lastId);
        }

        RefreshNestUiKeepSheet(lastId, _activeNestSheet);
        RefreshStockMaterialCards();
        SetStatus($"已复制 {ids.Count} 件到待用区");
    }

    void OnNestHoldDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null) return;
        var ids = NestContextIds();
        if (ids.Count == 0) return;

        var panels = ids
            .Select(id => _session.Package.Panels.FirstOrDefault(p => p.PanelId == id))
            .OfType<PanelPart>()
            .ToList();
        if (panels.Count == 0) return;

        var anyFusion = panels.Any(p =>
            !string.Equals(p.Identity?.SourceFormat, "draft", StringComparison.OrdinalIgnoreCase));
        if (anyFusion)
        {
            var ask = MessageBox.Show(
                this,
                $"删除待用区 {panels.Count} 件？\n其中有从方案载入的板，删除后无法再排。",
                "删除待用区板件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;
        }

        foreach (var id in ids)
        {
            _session.RemovePanel(id);
            _nestHolding.RemoveAll(h => h.PanelId == id);
            if (_nest is not null)
            {
                foreach (var place in _nest.Placements.Where(p => p.PanelId == id).ToList())
                    _nest.Placements.Remove(place);
            }
            _nestSelected.Remove(id);
            _locked.Remove(id);
        }

        _selected = _session.Package.Panels.FirstOrDefault(p => p.PanelId == _selected?.PanelId);
        RefreshNestUiKeepSheet(_selected?.PanelId, _activeNestSheet);
        RefreshStockMaterialCards();
        SetStatus($"已删除待用区 {ids.Count} 件");
    }

    void OnNestChangeMaterialClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true })
            return;
        var hitId = _nestContextPanelId;
        if (string.IsNullOrWhiteSpace(hitId))
            return;

        var ids = _nestSelected.Contains(hitId) && _nestSelected.Count > 1
            ? _nestSelected.ToList()
            : [hitId];
        var selectedPanels = ids
            .Select(id => _session.Package.Panels.FirstOrDefault(p => p.PanelId == id))
            .OfType<PanelPart>()
            .ToList();
        if (selectedPanels.Count == 0)
        {
            SetStatus("未找到板件");
            return;
        }

        RefreshStockMaterialCards();
        if (_stockKinds.Count == 0)
        {
            SetStatus("当前方案没有材料种类");
            return;
        }

        var options = _stockKinds
            .Select(k => new MaterialKindOption
            {
                Key = NestGroupKey.From(k.MaterialId, k.ThicknessMm),
                Label = string.IsNullOrWhiteSpace(k.Label) ? k.MaterialId : k.Label.Trim(),
                PanelCount = k.PanelCount,
            })
            .ToList();
        var prefer = NestGroupKey.From(selectedPanels[0].Material, selectedPanels[0].ThicknessMm);
        var dlg = new ChangeMaterialWindow(options, selectedPanels, prefer) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ChosenKey is not { } target)
            return;

        if (selectedPanels.All(p => MaterialCorrect.SameKind(p, target)))
        {
            SetStatus("材料未改");
            return;
        }

        if (!_session.TryChangePanelMaterials(ids, target, dlg.BlindPolicy))
        {
            SetStatus("改变材料失败");
            return;
        }

        foreach (var id in ids)
        {
            _locked.Remove(id);
            _retargetFocusIds.Add(id);
        }

        if (_selected is not null)
            _selected = _session.Package.Panels.FirstOrDefault(p => p.PanelId == _selected.PanelId) ?? _selected;

        _opsOverlay = [];
        ResetProfileBridges();
        ExitBridgeManualMode();
        if (NcPreview is not null)
            NcPreview.Text = "";

        RefreshStockMaterialCards();
        BindPartList(_selected?.PanelId);
        RefreshGeomRail();
        RefreshNestReport();
        RefreshWorkflowDots();
        RefreshOneClickExport();
        CanvasHost.InvalidateVisual();

        var label = options.FirstOrDefault(o => o.Key.Equals(target))?.Label ?? target.ToString();
        SetStatus($"已改为 {label} · {selectedPanels.Count} 件 · 可继续改其他板，改完后点「重新密排」");
    }

    void FocusRetargetedPlacements()
    {
        if (_retargetFocusIds.Count == 0 || _nest is not { Ok: true })
        {
            _retargetFocusIds.Clear();
            return;
        }

        var place = _nest.Placements.FirstOrDefault(p => _retargetFocusIds.Contains(p.PanelId));
        if (place is not null)
        {
            _activeNestSheet = place.SheetIndex;
            _nestSelected.Clear();
            foreach (var id in _retargetFocusIds)
            {
                if (_nest.Placements.Any(p => p.PanelId == id))
                    _nestSelected.Add(id);
            }
            SyncPartListFromNestSelection(place.PanelId);
        }
        _retargetFocusIds.Clear();
        UpdateNestSheetChrome();
    }

    HwndSource? _hwndSource;

    const int WmKeyDown = 0x0100;
    const int WmKeyUp = 0x0101;
    const int WmSysKeyDown = 0x0104;
    const int WmSysKeyUp = 0x0105;
    const int WmSysCommand = 0x0112;
    const int ScKeyMenu = 0xF100;
    const int VkMenu = 0x12;
    const int VkLMenu = 0xA4;
    const int VkRMenu = 0xA5;
    const int VkD = 0x44;
    const int VkS = 0x53;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Alt otherwise enters Win32 menu mode and mouse-drag stalls for ~500ms.
        if (msg == WmSysCommand && ((int)wParam & 0xFFF0) == ScKeyMenu)
        {
            if (_dragMode == "nest" || _stage == "nest")
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        if (msg is WmSysKeyDown or WmSysKeyUp)
        {
            var vk = (int)wParam.ToInt64() & 0xFFFF;
            if (vk is VkMenu or VkLMenu or VkRMenu or VkS)
            {
                if (vk == VkS && (IsTypingTarget() || _dragMode != "nest"))
                    return IntPtr.Zero;
                if (_dragMode == "nest" || (vk != VkS && _stage == "nest"))
                {
                    var repeat = msg == WmSysKeyDown && ((lParam.ToInt64() >> 30) & 1) != 0;
                    if (_dragMode == "nest" && !repeat)
                        ApplyNestDragAtLastPointer();
                    handled = true;
                    return IntPtr.Zero;
                }
            }
        }
        if (msg is WmKeyDown or WmKeyUp)
        {
            var vk = (int)wParam.ToInt64() & 0xFFFF;
            if (vk is VkD or VkS)
            {
                var repeat = msg == WmKeyDown && ((lParam.ToInt64() >> 30) & 1) != 0;
                if (vk == VkS)
                {
                    if (!IsTypingTarget() && _dragMode == "nest")
                    {
                        if (!repeat)
                            ApplyNestDragAtLastPointer();
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                if (vk == VkD)
                {
                    if (!repeat)
                        CanvasHost.InvalidateVisual();
                    if (msg == WmKeyDown
                        && _stage is "nest" or "ops"
                        && _nestSelected.Count == 2
                        && Keyboard.Modifiers == ModifierKeys.None
                        && !IsTypingTarget())
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }
        }
        return IntPtr.Zero;
    }

    static bool AltIsDown() =>
        (GetAsyncKeyState(VkMenu) & 0x8000) != 0
        || (GetAsyncKeyState(VkLMenu) & 0x8000) != 0
        || (GetAsyncKeyState(VkRMenu) & 0x8000) != 0;

    static bool DIsDown() => (GetAsyncKeyState(VkD) & 0x8000) != 0;

    static bool SIsDown() => (GetAsyncKeyState(VkS) & 0x8000) != 0;

    static bool IsAltKey(KeyEventArgs e) =>
        e.Key is Key.LeftAlt or Key.RightAlt
        || e.SystemKey is Key.LeftAlt or Key.RightAlt;

    static bool IsSKey(KeyEventArgs e) =>
        e.Key == Key.S || e.SystemKey == Key.S;

    string NestDragStatus(int groupCount, string? id)
    {
        var hint = "拖动中右键转90° · Alt 上下左右 · S 硬约束";
        if (SIsDown() && AltIsDown())
            hint = "拖动中右键转90° · Alt+S 轴锁硬约束";
        else if (SIsDown())
            hint = "拖动中右键转90° · S 硬约束";
        else if (AltIsDown())
            hint = "拖动中右键转90° · Alt 上下左右";
        if (groupCount > 1)
            return $"拖动 {groupCount} 件 · {hint}";
        return $"拖摆位 {id} · {hint}";
    }

    List<NestDrag.SlideMember> BuildSlideMembers(
        IReadOnlyDictionary<string, PanelPart> byId,
        IReadOnlySet<string> movingIds,
        string grabbedId)
    {
        var list = new List<NestDrag.SlideMember>();
        if (!_nestGroupOrig.TryGetValue(grabbedId, out var grabOrig)
            && _nest is { Ok: true })
        {
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == grabbedId);
            if (place is not null)
                grabOrig = (place.OffsetX, place.OffsetY, place.RotationDeg);
        }
        foreach (var id in movingIds)
        {
            if (!byId.TryGetValue(id, out var panel)) continue;
            if (_nestGroupOrig.TryGetValue(id, out var orig))
            {
                list.Add(new NestDrag.SlideMember(
                    id, panel, orig.Ox - grabOrig.Ox, orig.Oy - grabOrig.Oy, orig.Rot));
                continue;
            }
            var place = _nest?.Placements.FirstOrDefault(p => p.PanelId == id);
            if (place is null) continue;
            list.Add(new NestDrag.SlideMember(
                id, panel, place.OffsetX - grabOrig.Ox, place.OffsetY - grabOrig.Oy, place.RotationDeg));
        }
        return list;
    }

    void RotateNestDragClockwise90()
    {
        if (_nestDragPanelId is null || _session.Package is null) return;
        if (!_session.Package.Panels.ToDictionary(p => p.PanelId).TryGetValue(_nestDragPanelId, out var panel))
            return;

        var oldRot = _nestDragRotDeg;
        var newRot = NestDrag.RotateClockwise90(oldRot);
        _nestDragRotDeg = newRot;

        if (_nestDragFromHold)
        {
            foreach (var id in HoldingDragIds())
            {
                var held = _nestHolding.FirstOrDefault(h => h.PanelId == id);
                if (held is not null)
                    held.RotationDeg = NestDrag.RotateClockwise90(held.RotationDeg);
            }
            _nestDragRotDeg = _nestHolding.FirstOrDefault(h => h.PanelId == _nestDragPanelId)?.RotationDeg ?? newRot;
            _holdSlideHasValid = false;
            UpdateHoldPreviewFromScreen(_lastCanvasX, _lastCanvasY);
            if (!_holdPreviewOnSheet)
                SetStatus($"旋转 {_nestDragRotDeg:0}° · {HoldingDragIds().Count} 件");
            CanvasHost.InvalidateVisual();
            return;
        }

        var rotateIds = _nestGroupOrig.Count > 1
            ? _nestGroupOrig.Keys.ToList()
            : [_nestDragPanelId];
        foreach (var id in rotateIds)
        {
            var from = id == _nestDragPanelId
                ? oldRot
                : (_nestGroupOrig.TryGetValue(id, out var o) ? o.Rot : (double?)null);
            RotateNestPlacement(id, from, null, keepDragAnchor: id == _nestDragPanelId);
            var place = _nest?.Placements.FirstOrDefault(p => p.PanelId == id);
            if (place is not null)
                _nestGroupOrig[id] = (place.OffsetX, place.OffsetY, place.RotationDeg);
        }
        _nestDragRotDeg = _nest?.Placements.FirstOrDefault(p => p.PanelId == _nestDragPanelId)?.RotationDeg ?? newRot;
    }

    void RotateNestPlacement(string panelId) =>
        RotateNestPlacement(panelId, null, null, keepDragAnchor: false);

    void RotateNestPlacement(string panelId, double? oldRot, double? newRot, bool keepDragAnchor)
    {
        if (_nest is not { Ok: true } || _session.Package is null) return;
        if (!_session.Package.Panels.ToDictionary(p => p.PanelId).TryGetValue(panelId, out var panel))
            return;
        var place = _nest.Placements.FirstOrDefault(p => p.PanelId == panelId);
        if (place is null) return;

        var from = oldRot ?? place.RotationDeg;
        var to = newRot ?? NestDrag.RotateClockwise90(from);
        var (ox, oy) = NestDrag.OffsetKeepingCenter(panel, place.OffsetX, place.OffsetY, from, to);
        place.RotationDeg = to;
        var (sw, sh, _) = ActiveSheetMetrics();
        if (!ScreenInHoldingBay(_lastCanvasX))
            (ox, oy) = NestDrag.ClampOnSheet(
                panel, ox, oy, to, sw, sh, ParseMm(NestBorderBox.Text, 15));
        place.OffsetX = ox;
        place.OffsetY = oy;
        if (keepDragAnchor)
        {
            _nestOrigOx = ox;
            _nestOrigOy = oy;
            var (mx, my) = ScreenToSheet(_lastCanvasX, _lastCanvasY);
            _nestStartMx = mx;
            _nestStartMy = my;
        }
        else
        {
            RefreshNestReport();
        }
        SetStatus($"旋转 {to:0}° · {panel.DisplayPartName}" + (keepDragAnchor ? " · Alt 上下左右" : ""));
        CanvasHost.InvalidateVisual();
    }

    void UpdateHoverCursor(float x, float y)
    {
        if (_stage == "ops" && _bridgeDeleteMode)
        {
            CanvasPane.Cursor = Cursors.No;
            return;
        }
        if (_stage == "ops" && _bridgeManualMode)
        {
            CanvasPane.Cursor = Cursors.Cross;
            return;
        }
        if (_stage == "load" && _selected is not null)
        {
            var w = _surfaceW > 0 ? _surfaceW : Math.Max(1, (int)(CanvasHost.ActualWidth * _dpiX));
            var h = _surfaceH > 0 ? _surfaceH : Math.Max(1, (int)(CanvasHost.ActualHeight * _dpiY));
            var view = GeomInteraction.BuildView(_selected, w, h);
            var hit = GeomInteraction.HitTest(_selected, view, x, y);
            if (hit is { Type: not "panel" })
            {
                CanvasPane.Cursor = Cursors.SizeAll;
                var hint = hit.Value.Type switch
                {
                    "hole" => $"瀛?{hit.Value.FeatureId}",
                    "groovePoint" => $"妲界 {hit.Value.FeatureId}",
                    "resize" => $"杈?{hit.Value.Edge}",
                    _ => hit.Value.Type,
                };
                if (_hoverHint != hint) { _hoverHint = hint; CanvasHost.InvalidateVisual(); }
                return;
            }
        }
        else if ((_stage is "nest") && _nest is { Ok: true })
        {
            EnsureNestViewMetrics();
            if (HitTestLabel(x, y) is not null)
            {
                CanvasPane.Cursor = Cursors.SizeAll;
                return;
            }
            if (HitTestHolding(x, y) is not null)
            {
                CanvasPane.Cursor = Cursors.SizeAll;
                return;
            }
            var id = HitTestNest(x, y);
            if (id is not null)
            {
                CanvasPane.Cursor = _locked.Contains(id) ? Cursors.No : Cursors.SizeAll;
                return;
            }
            if (ScreenInHoldingBay(x))
            {
                CanvasPane.Cursor = Cursors.Hand;
                return;
            }
        }
        CanvasPane.Cursor = Cursors.Arrow;
        if (_hoverHint is not null) { _hoverHint = null; CanvasHost.InvalidateVisual(); }
    }

    void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        CanvasPixelPos(e);
        // Chord-click: pressing right while left is down can raise a spurious left-up.
        if (Mouse.LeftButton == MouseButtonState.Pressed)
            return;
        EndCanvasDrag();
    }

    void OnCanvasLostCapture(object sender, MouseEventArgs e)
    {
        if (_simPanning && e.MiddleButton == MouseButtonState.Pressed)
        {
            if (!CanvasPane.IsMouseCaptured)
                CanvasPane.CaptureMouse();
            return;
        }
        if (_simPanning)
            EndSimPan();
        if (Mouse.LeftButton == MouseButtonState.Pressed && _dragMode is not null)
        {
            if (!CanvasPane.IsMouseCaptured)
                CanvasPane.CaptureMouse();
            return;
        }
        EndCanvasDrag();
    }

    float _lastCanvasX, _lastCanvasY;

    void EndCanvasDrag()
    {
        if (_dragMode is null) return;

        if (_dragMode == "geom" && _selected is not null && _geomStart is not null)
        {
            if (!ReferenceEquals(_selected, _geomStart))
            {
                var draft = _selected;
                _selected = _geomStart;
                CommitPanel(draft);
                SetStatus("已更新板件几何 · 密排与刀路需要重新生成", StatusKind.Warning);
            }
        }
        else if (_dragMode == "nestBox")
        {
            FinishNestBoxSelect();
        }
        else if (_dragMode == "nest" && _nestDragPanelId is not null && _nest is { Ok: true } && _session.Package is not null)
        {
            FinishNestDrag(_nestDragPanelId, _nestDragFromHold, _lastCanvasX, _lastCanvasY);
        }

        _dragMode = null;
        _geomHit = null;
        _geomStart = null;
        _nestDragPanelId = null;
        _nestDragFromHold = false;
        _holdPreviewOnSheet = false;
        _holdPreviewBlocked = false;
        _holdSlideHasValid = false;
        _holdPreviewPlaces.Clear();
        _nestDragRotDeg = 0;
        _nestGroupOrig.Clear();
        if (CanvasPane.IsMouseCaptured) CanvasPane.ReleaseMouseCapture();
        CanvasHost.InvalidateVisual();
    }

    void FinishNestDrag(string panelId, bool fromHold, float sx, float sy)
    {
        if (_session.Package is null || _nest is not { Ok: true }) return;
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        if (!byId.TryGetValue(panelId, out var panel)) return;

        if (fromHold)
        {
            if (!_holdPreviewOnSheet)
            {
                SetStatus(ScreenInHoldingBay(sx)
                    ? $"仍在待用区 · 请拖到左侧大板上再松开"
                    : $"请拖到当前大板区域内再松开");
                return;
            }
            TryReturnHeldToSheet(panelId, panel, sx, sy);
            return;
        }

        // From sheet → holding bay (whole multi-select group)
        if (ScreenInHoldingBay(sx))
        {
            var ids = _nestGroupOrig.Count > 1
                ? _nestGroupOrig.Keys.ToList()
                : [panelId];
            var n = 0;
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var p)) continue;
                if (MovePlacementToHolding(id, p, refresh: false))
                    n++;
            }
            foreach (var id in ids)
                _nestSelected.Remove(id);
            RefreshNestReport();
            SetStatus(n <= 1
                ? $"已移入待用区 · {panel.DisplayPartName}"
                : $"已移入待用区 · {n} 件");
            CanvasHost.InvalidateVisual();
            return;
        }

        if (_nestGroupOrig.Count > 1)
        {
            FinishNestGroupDrag(panelId);
            return;
        }

        var place = _nest.Placements.FirstOrDefault(p => p.PanelId == panelId);
        if (place is null) return;
        var (sw, sh, _) = ActiveSheetMetrics();
        var spacing = ActiveSheetSpacingMm();
        var inset = ActiveSheetInsets();
        var trueShape = UsesTrueShapeNest();
        var allow = AllowOverlapChk.IsChecked == true;
        var ignore = PipIgnorePairs();
        var others = _nest.Placements
            .Where(p => p.PanelId != panelId)
            .Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
            .ToList();
        var desiredOx = place.OffsetX;
        var desiredOy = place.OffsetY;
        var (ox, oy, blocked) = NestDrag.Resolve(
            panel, panelId, desiredOx, desiredOy, place.RotationDeg, place.SheetIndex,
            others, byId, sw, sh, spacing, inset,
            (_nestOrigOx, _nestOrigOy), allow, ignore, trueShape);
        if (blocked && !allow)
        {
            var members = BuildSlideMembers(byId, new HashSet<string>(StringComparer.Ordinal) { panelId }, panelId);
            (ox, oy) = NestDrag.SlideTo(
                members, panelId,
                _nestOrigOx, _nestOrigOy, desiredOx, desiredOy,
                place.SheetIndex, others, byId, sw, sh, spacing, inset,
                _nestOrigOx, _nestOrigOy, ignore);
            (_, _, blocked) = NestDrag.Resolve(
                panel, panelId, ox, oy, place.RotationDeg, place.SheetIndex,
                others, byId, sw, sh, spacing, inset,
                (_nestOrigOx, _nestOrigOy), allow, ignore, trueShape);
        }
        (ox, oy) = ClampPipChild(panelId, panel, ox, oy, place.RotationDeg);
        place.OffsetX = ox;
        place.OffsetY = oy;
        SetStatus(blocked
            ? "冲突，已退回原位"
            : $"已移动 · {panel.DisplayPartName}");
        RefreshNestReport(full: false);
        CanvasHost.InvalidateVisual();
    }

    void CaptureNestGroupOrig()
    {
        _nestGroupOrig.Clear();
        if (_nest is null) return;
        foreach (var id in _nestSelected)
        {
            if (_locked.Contains(id)) continue;
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == id && p.SheetIndex == _activeNestSheet);
            if (place is null) continue;
            _nestGroupOrig[id] = (place.OffsetX, place.OffsetY, place.RotationDeg);
        }
        if (_nestDragPanelId is not null && !_nestGroupOrig.ContainsKey(_nestDragPanelId))
        {
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == _nestDragPanelId);
            if (place is not null)
                _nestGroupOrig[_nestDragPanelId] = (place.OffsetX, place.OffsetY, place.RotationDeg);
        }
        foreach (var slot in _partInPartSlots)
        {
            if (!slot.Enabled || !_nestGroupOrig.ContainsKey(slot.HostPanelId)) continue;
            if (_nestGroupOrig.ContainsKey(slot.ChildPanelId)) continue;
            if (_locked.Contains(slot.ChildPanelId)) continue;
            var child = _nest.Placements.FirstOrDefault(p => p.PanelId == slot.ChildPanelId);
            if (child is null) continue;
            _nestGroupOrig[slot.ChildPanelId] = (child.OffsetX, child.OffsetY, child.RotationDeg);
        }
    }

    void SyncPartListFromNestSelection(string? preferId)
    {
        var id = preferId is not null && _nestSelected.Contains(preferId)
            ? preferId
            : _nestSelected.FirstOrDefault();
        var panel = id is null
            ? null
            : _session.Package?.Panels.FirstOrDefault(p => p.PanelId == id);
        _syncingNestSelection = true;
        try
        {
            PartList.SelectedItem = panel;
            _selected = panel;
        }
        finally
        {
            _syncingNestSelection = false;
        }
    }

    void FinishNestBoxSelect()
    {
        if (_nest is not { Ok: true } || _session.Package is null) return;
        var dx = _nestBoxX1 - _nestBoxX0;
        var dy = _nestBoxY1 - _nestBoxY0;
        if (Math.Sqrt(dx * dx + dy * dy) < 3)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
            {
                _nestSelected.Clear();
                SyncPartListFromNestSelection(null);
                SetStatus("已取消选中");
            }
            CanvasHost.InvalidateVisual();
            return;
        }

        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        var parts = new List<(string Id, double MinX, double MinY, double MaxX, double MaxY)>();
        foreach (var place in _nest.Placements.Where(p => p.SheetIndex == _activeNestSheet))
        {
            if (_locked.Contains(place.PanelId)) continue;
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            parts.Add((place.PanelId, box.MinX, box.MinY, box.MaxX, box.MaxY));
        }
        var hits = NestDrag.BoxSelect(parts, _nestBoxX0, _nestBoxY0, _nestBoxX1, _nestBoxY1);
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!additive)
            _nestSelected.Clear();
        foreach (var id in hits)
            _nestSelected.Add(id);
        SyncPartListFromNestSelection(hits.FirstOrDefault());
        var crossing = NestDrag.IsCrossingSelect(_nestBoxX0, _nestBoxX1);
        SetStatus(hits.Count == 0
            ? (crossing ? "交叉选择：未碰到板件" : "窗口选择：无全包围板件")
            : _nestSelected.Count == 2
                ? $"{(crossing ? "交叉" : "窗口")}选择 · 2 件 · 按住 D 看间距"
                : $"{(crossing ? "交叉" : "窗口")}选择 · {_nestSelected.Count} 件");
        CanvasHost.InvalidateVisual();
    }

    void FinishNestGroupDrag(string grabbedId)
    {
        if (_nest is null || _session.Package is null) return;
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        var groupIds = _nestGroupOrig.Keys.ToHashSet(StringComparer.Ordinal);
        var (sw, sh, _) = ActiveSheetMetrics();
        var spacing = ActiveSheetSpacingMm();
        var inset = ActiveSheetInsets();
        var allow = AllowOverlapChk.IsChecked == true;
        var trueShape = UsesTrueShapeNest();
        var ignore = PipIgnorePairs();
        var others = _nest.Placements
            .Where(p => !groupIds.Contains(p.PanelId))
            .Select(p => (p.PanelId, p.SheetIndex, p.OffsetX, p.OffsetY, p.RotationDeg))
            .ToList();

        var revert = false;
        foreach (var id in groupIds)
        {
            var place = _nest.Placements.FirstOrDefault(p => p.PanelId == id);
            if (place is null || !byId.TryGetValue(id, out var panel)) continue;
            var orig = _nestGroupOrig[id];
            var (_, _, blocked) = NestDrag.Resolve(
                panel, id, place.OffsetX, place.OffsetY, place.RotationDeg, place.SheetIndex,
                others, byId, sw, sh, spacing, inset,
                (orig.Ox, orig.Oy), allow, ignore, trueShape);
            if (blocked)
            {
                revert = true;
                break;
            }
        }
        if (revert)
        {
            foreach (var (id, orig) in _nestGroupOrig)
            {
                var place = _nest.Placements.FirstOrDefault(p => p.PanelId == id);
                if (place is null) continue;
                place.OffsetX = orig.Ox;
                place.OffsetY = orig.Oy;
                place.RotationDeg = orig.Rot;
            }
            SetStatus("冲突，已退回原位");
        }
        else
        {
            var grabbed = byId.TryGetValue(grabbedId, out var gp) ? gp.DisplayPartName : grabbedId;
            SetStatus($"已移动 {_nestGroupOrig.Count} 件 · {grabbed}");
        }
        RefreshNestReport(full: false);
        CanvasHost.InvalidateVisual();
    }

    bool MovePlacementToHolding(string panelId, PanelPart panel, bool refresh = true)
    {
        if (_nest is null) return false;
        var place = _nest.Placements.FirstOrDefault(p => p.PanelId == panelId);
        if (place is null) return false;
        var (w, h) = NestDrag.SizeRotated(panel, place.RotationDeg);
        _nest.Placements.Remove(place);
        _nestHolding.RemoveAll(hld => hld.PanelId == panelId);
        _nestHolding.Add(new HeldNestPart
        {
            PanelId = panelId,
            Material = panel.Material ?? "",
            ThicknessMm = panel.ThicknessMm,
            RotationDeg = place.RotationDeg,
            WidthMm = w,
            HeightMm = h,
        });
        if (refresh)
        {
            SetStatus($"已移入待用区 · {panel.DisplayPartName}");
            RefreshNestReport();
            CanvasHost.InvalidateVisual();
        }
        return true;
    }

    void TryReturnHeldToSheet(string panelId, PanelPart panel, float sx, float sy)
    {
        if (_nest is null || _session.Package is null) return;
        var places = _holdPreviewPlaces.ToList();
        if (places.Count == 0) return;

        var sheetKey = ActiveSheetGroupKey();
        foreach (var p in places)
        {
            var held = _nestHolding.FirstOrDefault(h => h.PanelId == p.PanelId);
            if (held is null) continue;
            var partKey = NestGroupKey.From(held.Material, held.ThicknessMm);
            if (SameNestMaterial(partKey, sheetKey)) continue;
            MessageBox.Show(this,
                $"材料不匹配，不能放回当前大板。\n\n板件：{partKey}\n当前大板 {_activeNestSheet + 1}：{sheetKey}\n\n请先用 ◀ ▶ 切到同材料大板，再拖回。",
                "待用区",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus($"材料不符 · 当前大板是 {sheetKey}");
            return;
        }

        if (_holdPreviewBlocked && AllowOverlapChk.IsChecked != true)
        {
            SetStatus($"位置放不下 · {places.Count} 件仍留在待用区");
            return;
        }

        foreach (var p in places)
        {
            _nestHolding.RemoveAll(h => h.PanelId == p.PanelId);
            _nest.Placements.Add(new NestPlacementMsg
            {
                PanelId = p.PanelId,
                SheetIndex = _activeNestSheet,
                OffsetX = p.Ox,
                OffsetY = p.Oy,
                RotationDeg = p.Rot,
            });
            _nestSelected.Add(p.PanelId);
        }
        SetStatus(places.Count > 1
            ? $"已放回大板 {_activeNestSheet + 1} · {places.Count} 件"
            : $"已放回大板 {_activeNestSheet + 1} · {panel.DisplayPartName}");
        RefreshNestReport();
        CanvasHost.InvalidateVisual();
    }

    static bool SameNestMaterial(NestGroupKey a, NestGroupKey b) =>
        Math.Abs(a.ThicknessMm - b.ThicknessMm) < 1e-6
        && string.Equals(a.Material, b.Material, StringComparison.OrdinalIgnoreCase);

    NestGroupKey ActiveSheetGroupKey()
    {
        // Prefer material of parts already on this sheet (authoritative for "same material").
        if (_nest is { Ok: true } && _session.Package is not null)
        {
            var onSheet = _nest.Placements.FirstOrDefault(p => p.SheetIndex == _activeNestSheet);
            if (onSheet is not null)
            {
                var p = _session.Package.Panels.FirstOrDefault(x => x.PanelId == onSheet.PanelId);
                if (p is not null)
                    return NestGroupKey.From(p.Material, p.ThicknessMm);
            }
        }
        if (_activeNestSheet >= 0 && _activeNestSheet < _nestSheetsUsed.Count)
        {
            var s = _nestSheetsUsed[_activeNestSheet];
            if (!string.IsNullOrWhiteSpace(s.Material) || s.ThicknessMm > 0)
                return NestGroupKey.From(s.Material, s.ThicknessMm);
        }
        return NestGroupKey.From(null, 0);
    }

    double ActiveSheetBorderMm()
    {
        if (_activeNestSheet >= 0 && _activeNestSheet < _nestSheetsUsed.Count)
            return Math.Max(0, _nestSheetsUsed[_activeNestSheet].BorderMm);
        return ParseMm(NestBorderBox.Text, 15);
    }

    SheetInsets ActiveSheetInsets()
    {
        if (_activeNestSheet >= 0 && _activeNestSheet < _nestSheetsUsed.Count)
            return _nestSheetsUsed[_activeNestSheet].Insets();
        return SheetInsets.Uniform(ParseMm(NestBorderBox.Text, 15));
    }

    double ActiveSheetSpacingMm()
    {
        if (_activeNestSheet >= 0 && _activeNestSheet < _nestSheetsUsed.Count
            && _nestSheetsUsed[_activeNestSheet].SpacingMm > 0)
            return _nestSheetsUsed[_activeNestSheet].SpacingMm;
        return ParseMm(NestSpacingBox.Text, 12);
    }

    (double Ox, double Oy)? FindFreeSlotOnSheet(
        PanelPart panel,
        double rotDeg,
        int sheetIndex,
        IReadOnlyList<(string PanelId, int SheetIndex, double Ox, double Oy, double Rot)> others,
        IReadOnlyDictionary<string, PanelPart> byId,
        double sheetW,
        double sheetH,
        double borderMm,
        double spacingMm)
    {
        var box = NestDrag.Aabb(panel, 0, 0, rotDeg);
        var partW = Math.Max(1, box.MaxX - box.MinX);
        var partH = Math.Max(1, box.MaxY - box.MinY);
        const double step = 20;
        var maxX = sheetW - borderMm - partW;
        var maxY = sheetH - borderMm - partH;
        if (maxX < borderMm || maxY < borderMm) return null;

        for (var y = borderMm; y <= maxY + 1e-6; y += step)
        for (var x = borderMm; x <= maxX + 1e-6; x += step)
        {
            var (_, _, blocked) = NestDrag.Resolve(
                panel, panel.PanelId, x, y, rotDeg, sheetIndex,
                others, byId, sheetW, sheetH, spacingMm, borderMm,
                (x, y), allowOverlap: false);
            if (!blocked) return (x, y);
        }
        return null;
    }

    bool ScreenInHoldingBay(float sx) =>
        _holdingBayLeft > 0 && sx >= _holdingBayLeft;

    bool ScreenOverNestSheet(float sx, float sy)
    {
        if (_nestScale <= 0) return false;
        var left = _nestOriginX;
        var top = _nestOriginY;
        var right = _nestOriginX + _nestSheetW * _nestScale;
        var bottom = _nestOriginY + _nestSheetH * _nestScale;
        return sx >= left && sx <= right && sy >= top && sy <= bottom;
    }

    string? HitTestHolding(float sx, float sy)
    {
        foreach (var it in _holdingLayout)
        {
            if (it.Box.Contains(sx, sy))
                return it.PanelId;
        }
        return null;
    }

    void EnsureNestViewMetrics()
    {
        if (_nestScale > 0 && _surfaceW > 0) return;
        var w = _surfaceW > 0 ? _surfaceW : Math.Max(1, (int)(CanvasHost.ActualWidth * _dpiX));
        var h = _surfaceH > 0 ? _surfaceH : Math.Max(1, (int)(CanvasHost.ActualHeight * _dpiY));
        var (sw, sh, _) = ActiveSheetMetrics();
        var bay = _stage == "nest" ? CanvasPainter.NestHoldingBayWidth : 0f;
        var pad = 44f;
        var availW = Math.Max(1f, w - bay - pad);
        var scale = Math.Min(availW / sw, (h - 2 * pad) / sh) * 0.9f;
        if (scale <= 0) return;
        _nestPad = pad;
        _nestScale = scale;
        _nestOriginX = pad;
        _nestOriginY = pad;
        _nestSheetW = sw;
        _nestSheetH = sh;
        _holdingBayLeft = bay > 0 ? w - bay : 0;
    }

    string? HitTestLabel(float sx, float sy)
    {
        if (_nest is not { Ok: true } || _session.Package is null) return null;
        EnsureNestViewMetrics();
        if (_nestScale <= 0) return null;
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        var (mx, my) = ScreenToSheet(sx, sy);
        var minHalfW = 7.0 / _nestScale;
        var minHalfH = 6.0 / _nestScale;
        string? best = null;
        var bestArea = double.MaxValue;
        foreach (var place in _nest.Placements.Where(p => p.SheetIndex == _activeNestSheet))
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            var anchor = ResolveLabelAnchor(panel, place.RotationDeg);
            var bounds = NestTransform.BoundsOf(panel);
            var (cx, cy) = NestTransform.ToSheet(
                anchor.LocalX, anchor.LocalY, bounds,
                place.OffsetX, place.OffsetY, place.RotationDeg);
            var halfW = Math.Max(anchor.WidthMm * 0.5, minHalfW);
            var halfH = Math.Max(anchor.HeightMm * 0.5, minHalfH);
            if (mx < cx - halfW || mx > cx + halfW || my < cy - halfH || my > cy + halfH)
                continue;
            var area = (halfW * 2) * (halfH * 2);
            if (area >= bestArea) continue;
            bestArea = area;
            best = place.PanelId;
        }
        return best;
    }

    void ApplyLabelDragFromScreen(float x, float y)
    {
        if (_dragMode != "label" || _nestDragPanelId is null
            || _nest is not { Ok: true } || _session.Package is null)
            return;
        var place = _nest.Placements.FirstOrDefault(p => p.PanelId == _nestDragPanelId);
        var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == _nestDragPanelId);
        if (place is null || panel is null) return;
        var (sx, sy) = ScreenToSheet(x, y);
        var bounds = NestTransform.BoundsOf(panel);
        var (lx, ly) = NestTransform.FromSheet(
            sx, sy, bounds, place.OffsetX, place.OffsetY, place.RotationDeg);
        var found = LabelAnchorFinder.Find(panel, place.RotationDeg, (lx, ly));
        _labelOverrides[panel.PanelId] = (found.LocalX, found.LocalY);
        SetStatus($"贴标落点 {found.LocalX:0.#},{found.LocalY:0.#}");
        CanvasHost.InvalidateVisual();
    }

    string? HitTestNest(float sx, float sy)
    {
        if (_nest is not { Ok: true } || _session.Package is null) return null;
        EnsureNestViewMetrics();
        var byId = _session.Package.Panels.ToDictionary(p => p.PanelId);
        var (lx, ly) = ScreenToSheet(sx, sy);
        string? best = null;
        var bestArea = double.MaxValue;
        foreach (var place in _nest.Placements.Where(p => p.SheetIndex == _activeNestSheet))
        {
            if (!byId.TryGetValue(place.PanelId, out var panel)) continue;
            var box = NestDrag.Aabb(panel, place.OffsetX, place.OffsetY, place.RotationDeg);
            const double pad = 2;
            if (lx < box.MinX - pad || lx > box.MaxX + pad || ly < box.MinY - pad || ly > box.MaxY + pad)
                continue;
            var area = Math.Max(1, (box.MaxX - box.MinX) * (box.MaxY - box.MinY));
            if (area < bestArea)
            {
                bestArea = area;
                best = place.PanelId;
            }
        }
        return best;
    }

    (double Mx, double My) ScreenToSheet(float sx, float sy)
    {
        if (_nestScale <= 0) return (0, 0);
        var ox = _nestOriginX;
        var oy = _nestOriginY;
        var mx = (sx - ox) / _nestScale;
        var my = _nestSheetH - (sy - oy) / _nestScale;
        return (mx, my);
    }

    static bool PointInOutline(double x, double y, PanelPart panel)
    {
        var pts = panel.Outline.Points;
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var xi = pts[i].X;
            var yi = pts[i].Y;
            var xj = pts[j].X;
            var yj = pts[j].Y;
            var hit = yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi;
            if (hit) inside = !inside;
        }
        return inside;
    }

    void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        RefreshDpi();
        _surfaceW = e.Info.Width;
        _surfaceH = e.Info.Height;
        var canvas = e.Surface.Canvas;
        if (AwaitingInitialNest())
        {
            canvas.Clear(SKColors.White);
            return;
        }
        if (_stage == "out" && !_showNest)
        {
            canvas.Clear(SKColors.White);
            return;
        }
        if (_showNest && _stage is "nest" or "ops" or "out")
        {
            var (sw, sh, _) = ActiveSheetMetrics();
            var bay = _stage == "nest" ? CanvasPainter.NestHoldingBayWidth : 0f;
            var (fitScale, pad) = CurrentNestFit();
            if (fitScale <= 0) return;
            // User zoom/pan applies on every stage that shows the sheet (CAD convention);
            // ResolveSimView falls back to the fit when the user has not zoomed.
            var (scale, ox, oy) = ResolveSimView(fitScale, pad);
            _nestPad = pad;
            _nestScale = scale;
            _nestOriginX = ox;
            _nestOriginY = oy;
            _nestSheetW = sw;
            _nestSheetH = sh;
            _holdingBayLeft = bay > 0 ? e.Info.Width - bay : 0;

            var placements = _nest is { Ok: true } ? _nest.Placements.ToList() : [];
            var panels = _session.Package?.Panels.ToList() ?? [];
            var byId = panels.ToDictionary(p => p.PanelId, StringComparer.Ordinal);
            if (bay > 0)
            {
                var holdCards = _nestHolding.Select(h =>
                {
                    byId.TryGetValue(h.PanelId, out var p);
                    var title = p?.DisplayPartName ?? h.PanelId;
                    var detail = $"{h.WidthMm:0.#}×{h.HeightMm:0.#}";
                    var key = NestGroupKey.From(h.Material, h.ThicknessMm);
                    var groupLabel = p is null ? key.ToString() : KindDisplayName(p);
                    IReadOnlyList<(double X, double Y)> outline = p?.Outline.Points is { Count: >= 2 } pts
                        ? NestTransform.RotatedOutline(
                            pts.Select(pt => (pt.X, pt.Y)).ToList(),
                            h.RotationDeg)
                        : [(0, 0), (h.WidthMm, 0), (h.WidthMm, h.HeightMm), (0, h.HeightMm)];
                    return (h.PanelId, title, detail, outline, key.ToString(), groupLabel);
                }).ToList();
                (_holdingLayout, _holdingRegions) = CanvasPainter.LayoutHoldingItems(
                    holdCards, _holdingBayLeft, bay, e.Info.Height);
            }
            else
            {
                _holdingLayout = [];
                _holdingRegions = [];
            }

            _guillotineBySheet.TryGetValue(_activeNestSheet, out var guill);
            (double X, double Y)? dragFrom = null;
            (double X, double Y)? dragTo = null;
            if (_dragMode == "nest"
                && !_nestDragFromHold
                && _nestDragPanelId is not null
                && AltIsDown())
            {
                var dragPlace = placements.FirstOrDefault(p => p.PanelId == _nestDragPanelId);
                if (dragPlace is not null && byId.TryGetValue(_nestDragPanelId, out var dragPanel))
                {
                    var (pw, ph) = NestDrag.SizeRotated(dragPanel, _nestDragRotDeg);
                    dragFrom = (_nestOrigOx + pw * 0.5, _nestOrigOy + ph * 0.5);
                    dragTo = (dragPlace.OffsetX + pw * 0.5, dragPlace.OffsetY + ph * 0.5);
                }
            }

            (double X, double Y)? measureFrom = null;
            (double X, double Y)? measureTo = null;
            if (IsActive
                && DIsDown()
                && !IsTypingTarget()
                && _nestSelected.Count == 2)
            {
                var ids = _nestSelected.ToList();
                var place0 = placements.FirstOrDefault(p =>
                    p.PanelId == ids[0] && p.SheetIndex == _activeNestSheet);
                var place1 = placements.FirstOrDefault(p =>
                    p.PanelId == ids[1] && p.SheetIndex == _activeNestSheet);
                if (place0 is not null && place1 is not null
                    && byId.TryGetValue(ids[0], out var pan0)
                    && byId.TryGetValue(ids[1], out var pan1)
                    && pan0.Outline.Points.Count >= 2
                    && pan1.Outline.Points.Count >= 2)
                {
                    var ring0 = NestTransform.SheetOutline(
                        pan0, place0.OffsetX, place0.OffsetY, place0.RotationDeg);
                    var ring1 = NestTransform.SheetOutline(
                        pan1, place1.OffsetX, place1.OffsetY, place1.RotationDeg);
                    var pair = PolygonDistance.Closest(ring0, ring1);
                    if (!double.IsNaN(pair.Distance))
                    {
                        measureFrom = (pair.A.X, pair.A.Y);
                        measureTo = (pair.B.X, pair.B.Y);
                    }
                }
            }

            CanvasPainter.PaintNest(canvas, e.Info.Width, e.Info.Height, panels, placements,
                new CanvasPainter.NestPaintOpts(
                    sw, sh, pad, scale,
                    _stage == "out" ? null : _selected?.PanelId,
                    _locked,
                    CurrentConflicts(),
                    _stage == "ops" ? _opsOverlay : null,
                    ShowOps: _stage == "ops",
                    ActiveCamFrame: _stage == "ops" && _camFrames.Count > 0
                        ? _camFrames[_camFrameIndex]
                        : null,
                    ActiveSheetIndex: _activeNestSheet,
                    GuillotinePolyline: _stage == "out" ? null : guill?.Polyline,
                    GuillotineLabel: _stage == "out" ? null : guill?.Label,
                    GuillotineCuts: _stage == "out" || guill is null
                        ? null
                        : guill.Cuts.Select(c => (c.Polyline, c.Label)).ToList(),
                    GuillotinePieceLabels: _stage == "out" || guill is null
                        ? null
                        : guill.Pieces
                            .Where(p => !string.IsNullOrWhiteSpace(p.Label))
                            .Select(p => (p.LabelX, p.LabelY, p.Label!))
                            .ToList(),
                    HoldingBayLeft: _stage == "nest" ? _holdingBayLeft : 0,
                    HoldingItems: _holdingLayout,
                    HoldingRegions: _holdingRegions,
                    HoldingDragId: _nestDragFromHold ? _nestDragPanelId : null,
                    HoldPreviews: _holdPreviewOnSheet ? _holdPreviewPlaces : null,
                    HoldPreviewBlocked: _holdPreviewBlocked,
                    DragGuideFrom: dragFrom,
                    DragGuideTo: dragTo,
                    MeasureFrom: measureFrom,
                    MeasureTo: measureTo,
                    SelectedIds: _stage == "out" ? null : _nestSelected,
                    SelectionBox: _dragMode == "nestBox"
                        ? (_nestBoxX0, _nestBoxY0, _nestBoxX1, _nestBoxY1)
                        : null,
                    SelectionCrossing: _dragMode == "nestBox"
                        && NestDrag.IsCrossingSelect(_nestBoxX0, _nestBoxX1),
                    HighlightPass: _stage == "out" ? null : _opsFocus,
                    HighlightStrategy: _stage == "out" ? null : _opsStrategy,
                    Bridges: _stage == "out" ? null : _profileBridges,
                    LabelOverrides: CurrentLabelOverrides(),
                    LitePaint: _stage == "out" || _dragMode is "nest" or "label" or "nestBox",
                    NcSimStrokes: _stage == "out" ? _ncSimStrokes : null,
                    NcSimTimeSec: _ncSimTime,
                    FaintParts: _stage == "out",
                    OriginX: ox,
                    OriginY: oy,
                    NcSimToolDiaMm: _stage == "out" ? ShopToolDiaByNum() : null,
                    SheetGrain: CurrentSheetGrain()));
            return;
        }

        CanvasPainter.PaintGeom(canvas, e.Info.Width, e.Info.Height, _selected, _hoverHint);
    }

    enum StatusKind { Info, Success, Warning, Error, Busy }

    /// <summary>
    /// Status line with severity inferred from the message. 147 call sites pass plain text;
    /// the keywords below sort them into success / warning / error so the operator gets a
    /// coloured glyph instead of a uniform grey line. Explicit callers use the overload.
    /// </summary>
    void SetStatus(string text) => SetStatus(text, InferStatusKind(text));

    void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusTime.Text = DateTime.Now.ToString("HH:mm");
        var (glyph, brush, bg) = kind switch
        {
            StatusKind.Success => ("\uE73E", "SuccessBrush", "SuccessSoftBrush"),
            StatusKind.Warning => ("\uE7BA", "WarningBrush", "WarningSoftBrush"),
            StatusKind.Error => ("\uEA39", "DangerBrush", "DangerSoftBrush"),
            StatusKind.Busy => ("\uE916", "InfoBrush", "InfoSoftBrush"),
            _ => ("\uE946", "TextSecondaryBrush", null),
        };
        StatusGlyph.Text = glyph;
        StatusGlyph.Foreground = (Brush)FindResource(brush);
        StatusText.Foreground = kind is StatusKind.Info or StatusKind.Busy
            ? (Brush)FindResource("TextBrush")
            : (Brush)FindResource(brush);
        StatusBar.Background = bg is null
            ? new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF1))
            : (Brush)FindResource(bg);
    }

    static StatusKind InferStatusKind(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return StatusKind.Info;
        if (text.EndsWith("…", StringComparison.Ordinal) || text.EndsWith("...", StringComparison.Ordinal)
            || text.Contains("中…", StringComparison.Ordinal))
            return StatusKind.Busy;
        string[] error = ["失败", "错误", "禁止", "无法", "不能", "没有 BMP", "异常", "拒绝", "无效", "找不到"];
        string[] warning = ["警告", "未通过", "作废", "失效", "请先", "尚未", "缺", "跳过", "不匹配", "超出"];
        string[] success = ["已导出", "已写入", "已保存", "已载入", "已计算", "成功", "完成", "通过", "就绪", "已应用", "已合并", "已删除", "已添加", "已更新", "已切换", "Saved"];
        foreach (var k in error) if (text.Contains(k, StringComparison.Ordinal)) return StatusKind.Error;
        foreach (var k in warning) if (text.Contains(k, StringComparison.Ordinal)) return StatusKind.Warning;
        foreach (var k in success) if (text.Contains(k, StringComparison.Ordinal)) return StatusKind.Success;
        return StatusKind.Info;
    }

    /// <summary>
    /// Non-blocking notification card, top-right of the canvas. Auto-dismisses (errors stay
    /// longer); an optional action button (e.g. 打开目录) is the main reason to use this over
    /// the status line.
    /// </summary>
    void ShowToast(string title, string? detail, StatusKind kind, string? actionText = null, Action? action = null)
    {
        var (glyph, brush, bg) = kind switch
        {
            StatusKind.Success => ("\uE73E", "SuccessBrush", "SuccessSoftBrush"),
            StatusKind.Warning => ("\uE7BA", "WarningBrush", "WarningSoftBrush"),
            StatusKind.Error => ("\uEA39", "DangerBrush", "DangerSoftBrush"),
            _ => ("\uE946", "InfoBrush", "InfoSoftBrush"),
        };
        var accent = (Brush)FindResource(brush);
        var card = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = accent,
            BorderThickness = new Thickness(4, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.18 },
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 16,
            Foreground = accent,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextBrush"),
        });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        if (actionText is not null && action is not null)
        {
            var act = new Button
            {
                Content = actionText,
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(6, 3, 6, 3),
                MinHeight = 24,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontWeight = FontWeights.SemiBold,
            };
            act.Click += (_, _) => { action(); ToastHost.Children.Remove(card); };
            body.Children.Add(act);
        }
        var close = new Button
        {
            Content = "\uE711",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(4),
            MinHeight = 0,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            ToolTip = "关闭",
        };
        close.Click += (_, _) => ToastHost.Children.Remove(card);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(body, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(icon);
        grid.Children.Add(body);
        grid.Children.Add(close);
        card.Child = grid;
        _ = bg;

        while (ToastHost.Children.Count >= 3)
            ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(card);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(kind is StatusKind.Error or StatusKind.Warning ? 12 : 6),
        };
        timer.Tick += (_, _) => { timer.Stop(); ToastHost.Children.Remove(card); };
        timer.Start();
    }

    void RefreshStaleBanner()
    {
        var show = _module == "production"
            && _session.Package is not null
            && _session.ManufacturingDirty
            && (_nest is not null || HasNcText())
            && _stage is not "load";
        StaleBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        StaleGotoNestBtn.Visibility = _stage == "nest" ? Visibility.Collapsed : Visibility.Visible;
    }

    static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // best effort — the path is already in the status line
        }
    }

    SheetGrainKind CurrentSheetGrain()
    {
        if (_session.Package is null || _nest is not { Ok: true })
            return SheetGrainKind.None;
        var place = _nest.Placements.FirstOrDefault(p => p.SheetIndex == _activeNestSheet);
        var panel = place is null
            ? null
            : _session.Package.Panels.FirstOrDefault(p => p.PanelId == place.PanelId);
        if (panel is null) return SheetGrainKind.None;
        var key = NestGroupKey.From(panel.Material, panel.ThicknessMm);
        var kind = _stockKinds.FirstOrDefault(k =>
            NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(key));
        return kind?.SheetGrain ?? SheetGrainKind.None;
    }

    string KindDisplayName(PanelPart panel)
    {
        var key = NestGroupKey.From(panel.Material, panel.ThicknessMm);
        var hit = _stockKinds.FirstOrDefault(k =>
            NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(key));
        return hit is not null && !string.IsNullOrWhiteSpace(hit.Label)
            ? hit.Label.Trim()
            : panel.MaterialGroupLabel;
    }

    string? KindAutoLabel(string materialId, double thicknessMm)
    {
        var key = NestGroupKey.From(materialId, thicknessMm);
        return _session.Package?.Panels
            .FirstOrDefault(p => NestGroupKey.From(p.Material, p.ThicknessMm).Equals(key))
            ?.MaterialGroupLabel;
    }

    void OnStockKindLabelChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: StockMaterialKindVm kind }
            && string.IsNullOrWhiteSpace(kind.Label)
            && !string.IsNullOrWhiteSpace(kind.AutoLabel))
            kind.Label = kind.AutoLabel;
        ApplyKindRenameSideEffects();
    }

    void OnProjectNameChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingProjectName || ProjectNameBox is null) return;
        _session.ProjectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text)
            ? null
            : ProjectNameBox.Text.Trim();
        ApplyProjectNameChrome();
        if (_stage == "out")
            RefreshExportFiles();
    }

    void SyncProjectNameBox()
    {
        if (ProjectNameBox is null) return;
        _syncingProjectName = true;
        ProjectNameBox.Text = _session.ProjectName ?? "";
        _syncingProjectName = false;
        ApplyProjectNameChrome();
    }

    void ApplyProjectNameChrome()
    {
        var empty = string.IsNullOrWhiteSpace(_session.ProjectName) && _session.Package is null;
        var name = _session.ResolvedProjectName;
        var dirty = !empty && HasUnsavedWork();
        Title = empty ? "OmniCam" : "OmniCam — " + name + (dirty ? " *" : "");
        if (ProjectNameBadge is not null)
            ProjectNameBadge.Text = empty ? "" : name + (dirty ? " *" : "");
    }

    // ----- unsaved-work model --------------------------------------------------------
    // The project file holds package + nest + CAM session. "Unsaved" therefore means the
    // fingerprint of that saveable content differs from the last open/save, with view-only
    // state (stage, active sheet, selection) excluded so switching tabs never looks like work.

    string? _savedWorkFingerprint;

    string WorkFingerprint()
    {
        if (_session.Package is null) return "";
        var s = CaptureProjectSession();
        s.Stage = "";
        s.ActiveNestSheet = 0;
        s.OpsAllSheets = true;
        s.ShowNest = false;
        s.SelectedExportFile = null;
        var nest = _nest is { Ok: true }
            ? string.Join(";", _nest.Placements.Select(p =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{p.PanelId}:{p.SheetIndex}:{p.OffsetX:0.###}:{p.OffsetY:0.###}:{p.RotationDeg:0.#}")))
            : "";
        var raw = string.Concat(
            _session.PackageJson ?? "", "\n",
            nest, "\n",
            ProjectSessionCodec.Serialize(s), "\n",
            _session.ResolvedProjectName, "\n",
            SelectedMachineId());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    bool HasUnsavedWork()
    {
        if (_session.Package is null) return false;
        try
        {
            return WorkFingerprint() != _savedWorkFingerprint;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Baseline after open/save: what is on screen now is what is on disk.</summary>
    void MarkWorkSaved()
    {
        try
        {
            _savedWorkFingerprint = _session.Package is null ? null : WorkFingerprint();
        }
        catch
        {
            _savedWorkFingerprint = null;
        }
        ApplyProjectNameChrome();
    }

    /// <summary>Standard close guard: save / discard / cancel when work would be lost.</summary>
    void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!HasUnsavedWork()) return;
        var r = MessageBox.Show(this,
            $"工程「{_session.ResolvedProjectName}」有未保存的改动（板件、密排或刀路）。\n\n关闭前保存吗？",
            "未保存的改动",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);
        if (r == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }
        if (r == MessageBoxResult.Yes && !TrySaveProjectInteractive())
            e.Cancel = true;
    }

    PanelPart? PanelOnSheet(int sheetIndex, IEnumerable<string?> opPanelIds)
    {
        if (_session.Package is null) return null;
        foreach (var id in opPanelIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var hit = _session.Package.Panels.FirstOrDefault(p => p.PanelId == id);
            if (hit is not null) return hit;
        }
        if (_nest is { Ok: true })
        {
            var pid = _nest.Placements.FirstOrDefault(p => p.SheetIndex == sheetIndex)?.PanelId;
            if (!string.IsNullOrWhiteSpace(pid))
                return _session.Package.Panels.FirstOrDefault(p => p.PanelId == pid);
        }
        return null;
    }
}
