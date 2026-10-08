using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Parts;
using CabinetNC.Infrastructure.Diagnostics;
using SkiaSharp;

namespace CabinetNC.Desktop;

enum EditCmd
{
    None,
    Move,
    Copy,
    Line,
    Trim,
    Extend,
}

enum EditPhase
{
    Idle,
    PickObjects,
    BasePoint,
    SecondPoint,
    /// <summary>LINE: collecting centreline vertices.</summary>
    DrawLine,
    /// <summary>LINE: vertices done, waiting for 深[,宽] / T.</summary>
    LineDepth,
    /// <summary>TRIM / EXTEND: choosing cutting edges / boundaries (Enter = all + outline).</summary>
    PickCutters,
    /// <summary>TRIM / EXTEND: clicking the groove piece to cut / the end to extend.</summary>
    PickTargets,
}

/// <summary>
/// One drawable / pickable thing on the editor canvas. The board outline uses
/// <see cref="PanelEdit.OutlineId"/> and is only hit-tested during TRIM.
/// </summary>
sealed record EditEntity(
    string? FeatureId,
    string Label,
    IReadOnlyList<WorldPt> Pts,
    bool Closed,
    bool IsCircle,
    WorldPt Center,
    double Radius,
    IReadOnlyList<WorldPt>? Strip,
    IReadOnlyList<IReadOnlyList<WorldPt>> Islands,
    double HalfWidthMm,
    bool IsCutout,
    bool IsGroove)
{
    public bool Selectable => FeatureId is not null;

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        if (IsCircle)
            return (Center.X - Radius, Center.Y - Radius, Center.X + Radius, Center.Y + Radius);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Pts)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return (minX, minY, maxX, maxY);
    }

    public double Area()
    {
        var (a, b, c, d) = Bounds();
        return Math.Max(0, c - a) * Math.Max(0, d - b);
    }
}

/// <summary>
/// 编辑板件 mode: the draft canvas hosts an existing <see cref="Panel"/> and exposes
/// AutoCAD-style SELECT / MOVE / COPY / ERASE on its features. The panel stays the
/// source of truth — every command goes through <see cref="PanelEdit"/> so FeatureId,
/// Purpose, FaceId, arcs and islands survive; the canvas is only a projection.
/// </summary>
public partial class PanelDraftWindow
{
    static readonly SKColor SelInk = new(0xE8, 0xC8, 0x4A);
    static readonly SKColor HoverInk = new(0x3D, 0xE2, 0xE2);
    static readonly SKColor FeatureInk = new(0xE2, 0x4A, 0x4A);
    static readonly SKColor ProfileInk = new(0x4A, 0x9A, 0xE8);
    static readonly SKColor CutoutInk = new(0x7A, 0xB8, 0xF0);

    /// <summary>Labels mix ids with 槽 / 口袋 / 通孔 — Consolas has no CJK glyphs, so pick a face that does.</summary>
    static readonly SKTypeface LabelTypeface =
        SKFontManager.Default.MatchCharacter("Consolas", SKFontStyle.Normal, null, '槽')
        ?? SKTypeface.FromFamilyName("Microsoft YaHei UI")
        ?? SKTypeface.Default;

    bool _editorOn;
    Panel? _editPanel;
    Panel? _editOriginal;
    readonly List<EditEntity> _entities = [];
    readonly HashSet<string> _selIds = new(StringComparer.Ordinal);
    readonly Stack<Panel> _editUndo = new();
    readonly Stack<Panel> _editRedo = new();
    EditCmd _editCmd;
    EditPhase _editPhase;
    WorldPt? _basePt;
    EditEntity? _hoverEnt;
    bool _boxDragging;
    WorldPt _boxStart;
    WorldPt _boxEnd;
    (float X, float Y) _boxDownScreen;
    /// <summary>LINE vertices collected so far (panel mm).</summary>
    readonly List<WorldPt> _editLine = [];
    /// <summary>LINE centreline awaiting its depth.</summary>
    List<Point2>? _editPendingPath;
    /// <summary>TRIM cutting edges / EXTEND boundaries; null = every feature + outline.</summary>
    HashSet<string>? _cutters;

    /// <summary>Set to true by <see cref="PrepareEdit"/>; the caller may read this to apply the same edit to sibling panels.</summary>
    public bool EditorMode => _editorOn;

    /// <summary>
    /// Point that rubber band, ortho and Tab-ΔX/ΔY hang off: MOVE/COPY base point, or the last
    /// LINE vertex. Null when no point-driven phase is active.
    /// </summary>
    WorldPt? EditDynAnchor => _editPhase switch
    {
        EditPhase.SecondPoint => _basePt,
        EditPhase.DrawLine when _editLine.Count > 0 => _editLine[^1],
        _ => null,
    };

    public void PrepareEdit(Panel panel)
    {
        _editorOn = true;
        _editMode = true;
        _seed = panel;
        _editOriginal = panel;
        _editPanel = panel;
        _editUndo.Clear();
        _editRedo.Clear();
        _selIds.Clear();
        _editCmd = EditCmd.None;
        _editPhase = EditPhase.Idle;
        _basePt = null;
        _chains.Clear();
        _current.Clear();

        Title = $"编辑板件 · {panel.DisplayTitle}";
        if (CommitBtn is not null)
        {
            CommitBtn.Content = "写回方案";
            CommitBtn.IsEnabled = true;
            CommitBtn.Opacity = 1;
        }
        DraftNameBox.Text = panel.Name ?? panel.DisplayTitle;
        DraftNameBox.IsReadOnly = true;
        DraftIdBox.Text = panel.PanelId;
        DraftIdBox.IsReadOnly = true;
        DrawToolBar.Visibility = Visibility.Collapsed;
        ModeRail.Visibility = Visibility.Visible;
        EditToolBar.Visibility = Visibility.Visible;
        EditImpactText.Visibility = Visibility.Visible;
        SetSnap(false);
        ApplyMode(PanelDraftMode.Feature);

        RebuildEntities();
        _viewReady = false;
        UsageLog.LogEvent("ui", "panel.edit.open", new Dictionary<string, object?>
        {
            ["panelId"] = panel.PanelId,
            ["name"] = panel.DisplayTitle,
            ["material"] = panel.Material,
            ["thicknessMm"] = panel.ThicknessMm,
            ["featureCount"] = panel.Features.Count,
            ["outlinePts"] = panel.Outline.Points.Count,
        });
        RefreshPrompt();
        Redraw();
    }

    // ---------------------------------------------------------------- projection

    void RebuildEntities()
    {
        _entities.Clear();
        if (_editPanel is not { } p) return;

        var ring = p.Outline.Points.Select(q => new WorldPt(q.X, q.Y)).ToList();
        if (ring.Count >= 3 && !NearPt(ring[0], ring[^1])) ring.Add(ring[0]);
        _entities.Add(new EditEntity(PanelEdit.OutlineId, "外框", ring, true, false, default, 0, null, [], 0, false, false));

        foreach (var f in p.Features)
            _entities.Add(ToEntity(f));

        _selIds.RemoveWhere(id => p.Features.All(f => f.FeatureId != id));
        _hoverEnt = null;
        RefreshImpactChrome();
    }

    static EditEntity ToEntity(PanelFeature f)
    {
        var depth = f.Through ? "通" : f.DepthMm is { } d ? $"d{FormatDim(d)}" : "";
        if (PanelEdit.IsHole(f) && f.Path is null && f.Profile is null)
        {
            var r = Math.Max(0.25, (f.DiameterMm ?? 0) * 0.5);
            var c = new WorldPt(f.X, f.Y);
            var pts = CircleRing(c, r, 96) ?? [c];
            var label = $"{f.FeatureId} ⌀{FormatDim(r * 2)} {depth}".Trim();
            return new EditEntity(f.FeatureId, label, pts, true, true, c, r, null, [], 0, false, false);
        }

        var src = f.Path is { Count: >= 2 } ? f.Path : f.Profile ?? [];
        var chain = src.Select(q => new WorldPt(q.X, q.Y)).ToList();
        var profileOnly = f.Path is not { Count: >= 2 } && f.Profile is { Count: >= 3 };
        var closed = chain.Count >= 3
            && (PanelEdit.IsCutout(f) || PanelEdit.IsPocket(f) || profileOnly || NearPt(chain[0], chain[^1]));
        if (closed && chain.Count >= 3 && !NearPt(chain[0], chain[^1])) chain.Add(chain[0]);

        if (PanelEdit.IsGroove(f) && !closed)
        {
            var strip = GrooveGeometry.DisplayOutline(f);
            var stripPts = strip.Count >= 3 ? strip.Select(q => new WorldPt(q.X, q.Y)).ToList() : null;
            if (stripPts is { Count: >= 3 } && !NearPt(stripPts[0], stripPts[^1])) stripPts.Add(stripPts[0]);
            var width = f.WidthMm is > 1e-9 ? f.WidthMm.Value : GrooveGeometry.InferWidthMm(f.Path, f.Profile);
            var label = $"{f.FeatureId} 槽 {depth}{(width > 0 ? $" w{FormatDim(width)}" : "")}".Trim();
            return new EditEntity(f.FeatureId, label, chain, false, false, default, 0, stripPts, [], width * 0.5, false, true);
        }

        var islands = (f.Holes ?? [])
            .Select(h =>
            {
                var pts = h.Select(q => new WorldPt(q.X, q.Y)).ToList();
                if (pts.Count >= 3 && !NearPt(pts[0], pts[^1])) pts.Add(pts[0]);
                return (IReadOnlyList<WorldPt>)pts;
            })
            .ToList();
        var isCut = PanelEdit.IsCutout(f);
        var kindName = isCut ? "通孔" : PanelEdit.IsPocket(f) ? "口袋" : f.Kind;
        var lbl = isCut ? $"{f.FeatureId} 通孔" : $"{f.FeatureId} {kindName} {depth}".Trim();
        return new EditEntity(f.FeatureId, lbl, chain, closed, false, default, 0, null, islands, 0, isCut, false);
    }

    void RefreshImpactChrome()
    {
        if (EditImpactText is null || _editOriginal is null || _editPanel is null) return;
        var impact = PanelEdit.ClassifyChange(_editOriginal, _editPanel);
        (EditImpactText.Text, EditImpactText.Foreground) = impact switch
        {
            EditImpact.None => ("无改动", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A))),
            EditImpact.FeaturesOnly => ($"仅特征改动 · 写回后保留密排，只重算刀路 · 可撤销 {_editUndo.Count} 步",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0xBF, 0x5A))),
            _ => ($"通孔/外框已改 · 写回后需重新密排 · 可撤销 {_editUndo.Count} 步",
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x9A, 0x3A))),
        };
    }

    // ---------------------------------------------------------------- commands

    static string CmdName(EditCmd c) => c switch
    {
        EditCmd.Move => "MOVE",
        EditCmd.Copy => "COPY",
        EditCmd.Line => "LINE",
        EditCmd.Trim => "TRIM",
        EditCmd.Extend => "EXTEND",
        _ => "",
    };

    void EditRefreshPrompt()
    {
        if (CommitBtn is not null)
        {
            CommitBtn.IsEnabled = true;
            CommitBtn.Opacity = 1;
            CommitBtn.ToolTip = "把改动写回方案里的这块板";
        }
        SyncEditToolButtons();
        var n = _selIds.Count;
        var name = CmdName(_editCmd);
        CommandPrompt.Text = _editPhase switch
        {
            EditPhase.PickObjects => $"命令: {name} 选择对象（点选 / 框选 / 输入特征号 / ALL）· 已选 {n} · Enter 继续:",
            EditPhase.BasePoint => $"命令: {name} 指定基点（点选，或 x,y）:",
            EditPhase.SecondPoint when _dynEdit == DynField.X =>
                $"命令: {name} 指定 ΔX mm（带符号 · Tab 切到 ΔY）:",
            EditPhase.SecondPoint when _dynEdit == DynField.Y =>
                $"命令: {name} 指定 ΔY mm（带符号 · Tab 切到 ΔX）:",
            EditPhase.SecondPoint => SecondPointPrompt(name),
            EditPhase.DrawLine when _editLine.Count == 0 =>
                $"命令: LINE [{LineLayerName()}] 指定第一点（点选，或 x,y）:",
            EditPhase.DrawLine when _dynEdit == DynField.X => "命令: LINE 指定 ΔX mm（带符号 · Tab 切到 ΔY）:",
            EditPhase.DrawLine when _dynEdit == DynField.Y => "命令: LINE 指定 ΔY mm（带符号 · Tab 切到 ΔX）:",
            EditPhase.DrawLine => LinePointPrompt(),
            EditPhase.LineDepth => LineDepthPrompt(),
            EditPhase.PickCutters => _editCmd == EditCmd.Trim
                ? $"命令: TRIM 选择切割边（点选 / 框选 / 特征号）· 已选 {n} · Enter = {(n > 0 ? "确定" : "全部特征 + 外框")}:"
                : $"命令: EXTEND 选择边界（点选 / 框选 / 特征号）· 已选 {n} · Enter = {(n > 0 ? "确定" : "全部特征 + 外框")}:",
            EditPhase.PickTargets => _editCmd == EditCmd.Trim
                ? $"命令: TRIM 点要剪掉的槽段或外框边（切割边 {CutterSummary()}）· Enter/Esc 结束:"
                : $"命令: EXTEND 点要延长的槽端（边界 {CutterSummary()}）· Enter/Esc 结束:",
            _ => n > 0
                ? $"命令: 已选 {n} 个特征 · 当前层 {LineLayerName()} · M 移动 · CO 复制 · E/Del 删除 · Esc 取消选择"
                : $"命令: 当前层 {LineLayerName()} · 点选 / 框选，或 M 移动 · CO 复制 · E 删除 · L 画线 · TR 修剪 · EX 延伸 · U 撤销 · 写回",
        };
    }

    static string LineLayerName(PanelDraftMode mode) => mode switch
    {
        PanelDraftMode.Feature => "Feature",
        PanelDraftMode.Guide => "辅助线",
        _ => "Profile",
    };

    string LineLayerName() => LineLayerName(_mode);

    string CutterSummary() =>
        _cutters is null ? "全部 + 外框" : $"{_cutters.Count} 个 + 外框";

    string LinePointPrompt()
    {
        var bits = new List<string>();
        if (_lockDx is { } lx) bits.Add($"ΔX {FormatSigned(lx)}");
        if (_lockDy is { } ly) bits.Add($"ΔY {FormatSigned(ly)}");
        var locked = bits.Count == 0 ? "" : $"（已锁定 {string.Join(" · ", bits)}）";
        var ortho = EditorOrtho ? " · Alt/ORTHO 正交" : " · 按住 Alt 正交";
        var finish = _editLine.Count >= 2 ? " · Enter/Esc 结束" : "";
        return $"命令: LINE [{LineLayerName()}] 指定下一点{locked} · @dx,dy / 距离 · Tab 输入 ΔX/ΔY{ortho} · U 退一点{finish}:";
    }

    string LineDepthPrompt()
    {
        var last = _lastFeatureDepth is { } d
            ? $" · 回车沿用 {FormatDim(d)}{(_lastGrooveWidth is { } w ? $",{FormatDim(w)}" : "")}"
            : "";
        return $"命令: LINE 指定槽深 mm（T=通切，可写 深,宽 如 8,6）{last}:";
    }

    string SecondPointPrompt(string name)
    {
        var bits = new List<string>();
        if (_lockDx is { } lx) bits.Add($"ΔX {FormatSigned(lx)}");
        if (_lockDy is { } ly) bits.Add($"ΔY {FormatSigned(ly)}");
        var locked = bits.Count == 0 ? "" : $"（已锁定 {string.Join(" · ", bits)}）";
        var ortho = EditorOrtho ? " · Alt/ORTHO 正交" : " · 按住 Alt 正交";
        return _editCmd == EditCmd.Copy
            ? $"命令: {name} 指定第二点{locked} · Tab 输入 ΔX/ΔY{ortho} · Enter 结束，可连续复制:"
            : $"命令: {name} 指定第二点{locked} · Tab 输入 ΔX/ΔY{ortho}:";
    }

    bool TryHandleMoveDynTab()
    {
        var back = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        if (_dynEdit == DynField.None)
        {
            FocusMoveDyn(back ? DynField.Y : DynField.X);
            return true;
        }
        if (!LockMoveDyn())
            return true;
        FocusMoveDyn(_dynEdit == DynField.X ? DynField.Y : DynField.X);
        return true;
    }

    bool LockMoveDyn()
    {
        var raw = (CommandBox.Text ?? "").Trim();
        if (EditDynAnchor is null) return false;
        if (raw.Length == 0)
        {
            var (dx, dy) = LiveMoveDelta() ?? (0, 0);
            raw = FormatDim2(_dynEdit == DynField.X ? dx : dy);
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            && !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        {
            CommandPrompt.Text = "命令: 无法识别位移（带符号数字，如 200 或 -50）";
            return false;
        }
        if (_dynEdit == DynField.X) _lockDx = v;
        else if (_dynEdit == DynField.Y) _lockDy = v;
        return true;
    }

    void FocusMoveDyn(DynField field)
    {
        _dynEdit = field;
        _dynTyped = false;
        var (dx, dy) = LiveMoveDelta() ?? (0, 0);
        var v = field == DynField.X ? (_lockDx ?? dx) : (_lockDy ?? dy);
        _syncingDim = true;
        CommandBox.Text = FormatDim2(v);
        _syncingDim = false;
        RefreshPrompt();
        Redraw();
        Dispatcher.BeginInvoke(() =>
        {
            CommandBox.Focus();
            CommandBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void HandleMoveDynEnter()
    {
        if (_dynEdit != DynField.None && !LockMoveDyn())
            return;
        _dynEdit = DynField.None;
        _dynTyped = false;
        if (_editPhase == EditPhase.DrawLine)
        {
            if (LiveMoveDest() is { } dest) AcceptEditLinePoint(dest);
            else RefreshPrompt();
            return;
        }
        if (LiveMoveDelta() is not { } d)
        {
            RefreshPrompt();
            return;
        }
        ApplyDisplacement(d.Dx, d.Dy);
    }

    void SyncMoveDynBox()
    {
        if (_dynEdit == DynField.None || _dynTyped || !MoveDynActive)
            return;
        var (dx, dy) = LiveMoveDelta() ?? (0, 0);
        var v = _dynEdit == DynField.X ? (_lockDx ?? dx) : (_lockDy ?? dy);
        var text = FormatDim2(v);
        if (CommandBox.Text == text) return;
        _syncingDim = true;
        CommandBox.Text = text;
        CommandBox.SelectAll();
        _syncingDim = false;
    }

    void SyncEditToolButtons()
    {
        if (EditToolSelect is null) return;
        EditToolSelect.IsChecked = _editCmd == EditCmd.None;
        EditToolMove.IsChecked = _editCmd == EditCmd.Move;
        EditToolCopy.IsChecked = _editCmd == EditCmd.Copy;
        EditToolLine.IsChecked = _editCmd == EditCmd.Line;
        EditToolTrim.IsChecked = _editCmd == EditCmd.Trim;
        EditToolExtend.IsChecked = _editCmd == EditCmd.Extend;
    }

    void StartEditCmd(EditCmd cmd)
    {
        if (_editPanel is null) return;
        if (_editCmd != EditCmd.None) EndEditCmd();
        _editCmd = cmd;
        _basePt = null;
        switch (cmd)
        {
            case EditCmd.Line:
                _editLine.Clear();
                _editPendingPath = null;
                _selIds.Clear();
                _editPhase = EditPhase.DrawLine;
                break;
            case EditCmd.Trim or EditCmd.Extend:
                // A pre-selection becomes the cutting-edge / boundary set, AutoCAD style.
                _cutters = null;
                _editPhase = EditPhase.PickCutters;
                break;
            default:
                _editPhase = _selIds.Count > 0 ? EditPhase.BasePoint : EditPhase.PickObjects;
                break;
        }
        CommandBox.Clear();
        RefreshPrompt();
        CommandBox.Focus();
        Redraw();
    }

    void EndEditCmd(string? message = null)
    {
        _editCmd = EditCmd.None;
        _editPhase = EditPhase.Idle;
        _basePt = null;
        _editLine.Clear();
        _editPendingPath = null;
        _cutters = null;
        ClearDyn();
        RefreshPrompt();
        if (message is not null) CommandPrompt.Text = message;
        Redraw();
    }

    /// <summary>Enter / Space / right-click with an empty command line.</summary>
    void EditSubmitEmpty()
    {
        switch (_editPhase)
        {
            case EditPhase.PickObjects:
                if (_selIds.Count == 0)
                {
                    RefreshPrompt();
                    CommandPrompt.Text = $"命令: {CmdName(_editCmd)} 还没选对象 · 点选 / 框选 / 输入特征号 / ALL:";
                    return;
                }
                _editPhase = EditPhase.BasePoint;
                RefreshPrompt();
                Redraw();
                return;
            case EditPhase.SecondPoint:
            case EditPhase.BasePoint:
                EndEditCmd();
                return;
            case EditPhase.DrawLine:
                FinishEditLine();
                return;
            case EditPhase.LineDepth:
                AcceptEditDepth("");
                return;
            case EditPhase.PickCutters:
                _cutters = _selIds.Count > 0 ? new HashSet<string>(_selIds, StringComparer.Ordinal) : null;
                _selIds.Clear();
                _editPhase = EditPhase.PickTargets;
                RefreshPrompt();
                Redraw();
                return;
            case EditPhase.PickTargets:
                EndEditCmd();
                return;
            default:
                RefreshPrompt();
                return;
        }
    }

    void EditSubmit(string raw)
    {
        if (raw.Length == 0)
        {
            EditSubmitEmpty();
            return;
        }
        var up = raw.ToUpperInvariant();

        // Inside LINE, "U" steps back one vertex (AutoCAD); depth answers are raw numbers / T.
        if (_editPhase == EditPhase.DrawLine && up is "U" or "UNDO")
        {
            if (_editLine.Count > 0) _editLine.RemoveAt(_editLine.Count - 1);
            ClearDyn();
            CommandBox.Clear();
            RefreshPrompt();
            Redraw();
            return;
        }
        if (_editPhase == EditPhase.LineDepth)
        {
            AcceptEditDepth(raw);
            return;
        }

        switch (up)
        {
            case "PANEL" or "DONE" or "写回" or "写回方案" or "加入" or "加入方案":
                OnCommitClick(this, new RoutedEventArgs());
                return;
            case "M" or "MOVE" or "移动":
                StartEditCmd(EditCmd.Move);
                return;
            case "CO" or "CP" or "COPY" or "复制":
                StartEditCmd(EditCmd.Copy);
                return;
            case "L" or "LINE" or "画线" or "画槽":
                StartEditCmd(EditCmd.Line);
                return;
            case "PROFILE" or "外框" or "轮廓":
                ApplyMode(PanelDraftMode.Profile);
                return;
            case "FEATURE" or "特征" or "槽层":
                ApplyMode(PanelDraftMode.Feature);
                return;
            case "GUIDE" or "辅助线" or "辅助":
                ApplyMode(PanelDraftMode.Guide);
                return;
            case "TR" or "TRIM" or "修剪":
                StartEditCmd(EditCmd.Trim);
                return;
            case "EX" or "EXTEND" or "延伸":
                StartEditCmd(EditCmd.Extend);
                return;
            case "E" or "ERASE" or "DEL" or "DELETE" or "删除":
                EraseSelected();
                return;
            case "U" or "UNDO" or "撤销":
                EditUndo();
                return;
            case "REDO" or "重做":
                EditRedo();
                return;
            case "ALL" or "全选":
                SelectAllFeatures();
                return;
            case "Z" or "ZOOM" or "FIT" or "适配":
                _viewReady = false;
                Redraw();
                RefreshPrompt();
                return;
            case "ESC" or "CANCEL":
                EditEscape();
                return;
        }

        if (_editPhase is EditPhase.BasePoint or EditPhase.SecondPoint or EditPhase.DrawLine)
        {
            if (TryParseEditPoint(raw, out var pt))
            {
                EditAcceptPoint(pt);
                return;
            }
            CommandPrompt.Text = "命令: 无法识别（x,y 或 @dx,dy 或 距离）";
            return;
        }

        // Bare feature id (so "feature4" works without hunting on the canvas). Same rule as a
        // click: idle → replaces the selection; inside a command's 选择对象 → toggles.
        if (_editPhase == EditPhase.PickTargets && PanelEdit.IsOutlineId(raw) && _editCmd == EditCmd.Trim)
        {
            ApplyTrimExtend(PanelEdit.OutlineId, _cursor ?? default);
            return;
        }
        var hit = _editPanel?.Features.FirstOrDefault(f => f.FeatureId.Equals(raw, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            if (_editPhase == EditPhase.PickTargets)
            {
                // Typed id: trim the middle / extend the far end — pick point = path midpoint.
                var mid = PanelEdit.FeatureCenterline(hit) is { Count: >= 2 } cl
                    ? CenterlineOps.At(cl, (cl.Count - 1) / 2.0)
                    : new Point2(hit.X, hit.Y);
                ApplyTrimExtend(hit.FeatureId, new WorldPt(mid.X, mid.Y));
                return;
            }
            if (_editPhase is EditPhase.PickObjects or EditPhase.PickCutters)
            {
                if (!_selIds.Remove(hit.FeatureId)) _selIds.Add(hit.FeatureId);
            }
            else
            {
                _selIds.Clear();
                _selIds.Add(hit.FeatureId);
            }
            RefreshPrompt();
            Redraw();
            return;
        }
        CommandPrompt.Text = "命令: 未知命令（M 移动 · CO 复制 · E 删除 · L 画线 · TR 修剪 · EX 延伸 · U 撤销 · ALL 全选 · 写回）";
    }

    bool TryParseEditPoint(string raw, out WorldPt pt)
    {
        pt = default;
        var rel = raw.StartsWith('@');
        var body = rel ? raw[1..] : raw;
        var parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (rel)
            {
                var anchor = EditDynAnchor ?? _basePt ?? _cursor ?? default;
                pt = new WorldPt(anchor.X + x, anchor.Y + y);
                return true;
            }
            pt = new WorldPt(x, y);
            return true;
        }
        if (parts.Length == 1 && !rel && EditDynAnchor is { } b
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dist))
        {
            // Direct distance entry along the cursor heading (ortho-snapped when ORTHO is on).
            var target = _cursor is { } c ? ResolvePoint(c, updateHover: false).Pt : new WorldPt(b.X + 1, b.Y);
            var dx = target.X - b.X;
            var dy = target.Y - b.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { dx = 1; dy = 0; len = 1; }
            pt = new WorldPt(b.X + dx / len * dist, b.Y + dy / len * dist);
            return true;
        }
        return false;
    }

    void EditAcceptPoint(WorldPt pt)
    {
        switch (_editPhase)
        {
            case EditPhase.BasePoint:
                _basePt = pt;
                _editPhase = EditPhase.SecondPoint;
                ClearDyn();
                CommandBox.Clear();
                RefreshPrompt();
                Redraw();
                return;
            case EditPhase.SecondPoint when _basePt is { } b:
                if (LiveMoveDelta() is { } live)
                    ApplyDisplacement(live.Dx, live.Dy);
                else
                    ApplyDisplacement(pt.X - b.X, pt.Y - b.Y);
                return;
            case EditPhase.DrawLine:
                AcceptEditLinePoint(_lockDx is not null || _lockDy is not null ? LiveMoveDest() ?? pt : pt);
                return;
        }
    }

    // ---------------------------------------------------------------- LINE (new groove)

    void AcceptEditLinePoint(WorldPt pt)
    {
        if (_editLine.Count > 0 && NearPt(_editLine[^1], pt))
        {
            CommandPrompt.Text = "命令: LINE 与上一点重合 · 再指定下一点:";
            return;
        }
        _editLine.Add(pt);
        ClearDyn();
        CommandBox.Clear();
        RefreshPrompt();
        Redraw();
    }

    void FinishEditLine()
    {
        if (_editPanel is null) return;
        if (_editLine.Count < 2)
        {
            EndEditCmd("命令: LINE 至少要 2 个点 · *取消*");
            return;
        }
        _editPendingPath = _editLine.Select(p => new Point2(p.X, p.Y)).ToList();
        ClearDyn();
        CommandBox.Clear();
        switch (_mode)
        {
            case PanelDraftMode.Profile:
                ApplyProfileLine();
                return;
            case PanelDraftMode.Guide:
                CommitEditGuide();
                return;
            default:
                BeginGrooveFromLine();
                return;
        }
    }

    static bool LineIsClosed(IReadOnlyList<WorldPt> pts) =>
        pts.Count >= 3 && NearPt(pts[0], pts[^1]) && UniqueWorldCount(pts) >= 3;

    void CommitEditGuide()
    {
        if (_editPendingPath is not { Count: >= 2 } path) return;
        var pts = path.Select(p => new WorldPt(p.X, p.Y)).ToList();
        _chains.Add(new DraftChain(PanelDraftMode.Guide, pts));
        UsageLog.LogEvent("ui", "panel.edit.line", new Dictionary<string, object?>
        {
            ["panelId"] = _editPanel?.PanelId,
            ["kind"] = "guide",
            ["pts"] = path.Count,
        });
        EndEditCmd($"命令: 已添加辅助线（{_chains.Count(c => c.Mode == PanelDraftMode.Guide)} 条）· 不进刀路 · L 继续画");
    }

    void BeginGrooveFromLine()
    {
        if (_editPanel is not { } cur || _editPendingPath is not { } path) return;
        var overlap = PanelEdit.ClassifyGrooveOverlap(cur, path, out var other);
        if (overlap == CenterlineOverlap.Duplicate)
        {
            EndEditCmd($"命令: LINE 与 {other} 完全重合 · 已取消（要改深度请选中它后重画；要改长度用 TR/EX）");
            return;
        }
        _editPhase = EditPhase.LineDepth;
        CommandBox.Clear();
        RefreshPrompt();
        if (overlap == CenterlineOverlap.Collinear)
            CommandPrompt.Text = $"命令: 注意 · 与 {other} 共线重叠，两条都会加工 · {LineDepthPrompt()[4..]}";
        Redraw();
        CommandBox.Focus();
    }

    void ApplyProfileLine()
    {
        if (_editPanel is not { } cur || _editPendingPath is not { } path) return;
        Panel? next;
        string? err;
        string kind;
        if (LineIsClosed(_editLine))
        {
            next = PanelEdit.ReplaceOutline(cur, path, out err);
            kind = "replace";
        }
        else
        {
            next = PanelEdit.NotchOutline(cur, path, out err);
            kind = "notch";
        }
        if (next is null)
        {
            EndEditCmd($"命令: LINE 外框: {err} · 已取消");
            return;
        }
        PushUndo(cur);
        _editPanel = next;
        RebuildEntities();
        CommandBox.Clear();
        UsageLog.LogEvent("ui", "panel.edit.line", new Dictionary<string, object?>
        {
            ["panelId"] = cur.PanelId,
            ["kind"] = $"outline-{kind}",
            ["pts"] = path.Count,
            ["impact"] = PanelEdit.ClassifyChange(cur, next).ToString(),
        });
        var off = PanelEdit.FeaturesOutsideOutline(next, next.Features.Select(f => f.FeatureId), 1.0).ToList();
        var extra = off.Count == 0 ? "" : $" · 注意 {string.Join(", ", off.Take(4))} 现在出板";
        EndEditCmd(kind == "replace"
            ? $"命令: 已替换外框{extra} · 写回后需重排 · L 继续画"
            : $"命令: 已按这条线切开外框{extra} · 写回后需重排 · L 继续画");
    }

    void AcceptEditDepth(string raw)
    {
        if (_editPanel is not { } cur || _editPendingPath is not { } path) return;
        double depth;
        double? width = null;
        var through = false;
        if (raw.Length == 0)
        {
            if (_lastFeatureDepth is not { } last)
            {
                CommandPrompt.Text = "命令: 必须写入槽深 mm（T=通切，可写 深,宽）";
                return;
            }
            depth = last;
            width = _lastGrooveWidth;
        }
        else if (raw.Equals("T", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("THROUGH", StringComparison.OrdinalIgnoreCase)
            || raw is "通" or "通切")
        {
            depth = BoardThickness();
            through = true;
        }
        else if (!TryParseDepth(raw, out depth, out width))
        {
            CommandPrompt.Text = "命令: 无法识别深度（数字，或 T=通切，可写 8,6）";
            return;
        }
        if (depth <= 0)
        {
            CommandPrompt.Text = "命令: 深度必须 > 0";
            return;
        }
        var w = width is > 0 ? width.Value : _lastGrooveWidth ?? 6;
        if (!through) _lastFeatureDepth = depth;
        _lastGrooveWidth = w;
        if (depth >= BoardThickness() - 1e-6) through = true;

        var next = PanelEdit.AddVerticalGroove(cur, path, w, depth, through);
        var newId = next.Features[^1].FeatureId;
        var outside = PanelEdit.FeatureOutsideMm(next, newId);
        if (outside > 1.0)
        {
            EndEditCmd($"命令: LINE 槽出板 {FormatDim(outside)}mm · 已取消");
            return;
        }

        PushUndo(cur);
        _editPanel = next;
        RebuildEntities();
        // Deliberately not selected: a following TR/EX would otherwise treat it as the only
        // cutting edge (pre-selection semantics), which is not what "L then TR" means.
        CommandBox.Clear();
        UsageLog.LogEvent("ui", "panel.edit.line", new Dictionary<string, object?>
        {
            ["panelId"] = cur.PanelId,
            ["kind"] = "groove",
            ["id"] = newId,
            ["depth"] = depth,
            ["width"] = w,
            ["through"] = through,
        });
        EndEditCmd($"命令: 已添加槽 {newId} 深 {FormatDim(depth)} 宽 {FormatDim(w)}{(through ? "（通）" : "")} · L 继续画");
    }

    // ---------------------------------------------------------------- TRIM / EXTEND

    void ApplyTrimExtend(string featureId, WorldPt pick)
    {
        if (_editPanel is not { } cur) return;
        var p = new Point2(pick.X, pick.Y);
        string? err;
        Panel? next;
        if (PanelEdit.IsOutlineId(featureId))
        {
            if (_editCmd == EditCmd.Extend)
            {
                RefreshPrompt();
                CommandPrompt.Text = "命令: EXTEND 不能延伸外框 · 要用 LINE P 切角，或 TRIM 点外框边";
                return;
            }
            next = PanelEdit.TrimOutline(cur, _cutters, p, out err);
        }
        else
        {
            next = _editCmd == EditCmd.Trim
                ? PanelEdit.TrimGroove(cur, featureId, _cutters, p, out err)
                : PanelEdit.ExtendGroove(cur, featureId, _cutters, p, out err);
        }
        if (next is null)
        {
            RefreshPrompt();
            var label = PanelEdit.IsOutlineId(featureId) ? "外框" : featureId;
            CommandPrompt.Text = $"命令: {CmdName(_editCmd)} {label}: {err} · 再点一个:";
            Redraw();
            return;
        }
        if (_editCmd == EditCmd.Extend && !PanelEdit.IsOutlineId(featureId)
            && PanelEdit.FeatureOutsideMm(next, featureId) > PanelEdit.FeatureOutsideMm(cur, featureId) + 0.5)
        {
            RefreshPrompt();
            CommandPrompt.Text = $"命令: EXTEND {featureId} 会出板 · 已取消，再点一个:";
            return;
        }
        PushUndo(cur);
        _editPanel = next;
        RebuildEntities();
        UsageLog.LogEvent("ui", _editCmd == EditCmd.Trim ? "panel.edit.trim" : "panel.edit.extend",
            new Dictionary<string, object?>
            {
                ["panelId"] = cur.PanelId,
                ["target"] = PanelEdit.IsOutlineId(featureId) ? "outline" : featureId,
                ["impact"] = PanelEdit.ClassifyChange(cur, next).ToString(),
            });
        var before = cur.Features.Count;
        var after = next.Features.Count;
        RefreshPrompt();
        CommandPrompt.Text = PanelEdit.IsOutlineId(featureId)
            ? "命令: TRIM 已改外框 · 写回后需重排 · 再点一个，或 Enter 结束:"
            : _editCmd == EditCmd.Trim
                ? after < before
                    ? $"命令: TRIM 已删掉 {featureId} 剩余部分 · 再点一个，或 Enter 结束:"
                    : after > before
                        ? $"命令: TRIM 已把 {featureId} 剪成 {after - before + 1} 段 · 再点一个，或 Enter 结束:"
                        : $"命令: TRIM 已剪短 {featureId} · 再点一个，或 Enter 结束:"
                : $"命令: EXTEND 已延长 {featureId} 到边界 · 再点一个，或 Enter 结束:";
        Redraw();
    }

    void ApplyDisplacement(double dx, double dy)
    {
        if (_editPanel is not { } cur) return;
        if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6)
        {
            CommandPrompt.Text = "命令: 位移为 0 · 再指定第二点:";
            return;
        }
        var ids = _selIds.ToList();
        // Panel order, so CopyFeatures' NewIds line up with their sources.
        var sources = cur.Features.Where(f => _selIds.Contains(f.FeatureId)).Select(f => f.FeatureId).ToList();
        Panel next;
        IReadOnlyList<string> touched;
        if (_editCmd == EditCmd.Copy)
        {
            (next, touched) = PanelEdit.CopyFeatures(cur, ids, dx, dy);
        }
        else
        {
            next = PanelEdit.TranslateFeatures(cur, ids, dx, dy);
            touched = sources;
        }

        // Reject only moves that push a feature further off the board than it already was.
        // Imported data sometimes overhangs a little (CAM tolerates 80mm) — that must stay
        // movable, but it must not become a licence to drop the copy anywhere.
        var outside = new List<string>();
        for (var i = 0; i < touched.Count && i < sources.Count; i++)
        {
            var before = PanelEdit.FeatureOutsideMm(cur, sources[i]);
            var after = PanelEdit.FeatureOutsideMm(next, touched[i]);
            if (after > 1.0 && after > before + 0.5)
                outside.Add(touched[i]);
        }
        if (outside.Count > 0)
        {
            CommandPrompt.Text =
                $"命令: Δ({FormatSigned(dx)},{FormatSigned(dy)}) 会让 {string.Join(", ", outside.Take(4))}{(outside.Count > 4 ? "…" : "")} 出板 · 已取消，再指定第二点:";
            Redraw();
            return;
        }

        PushUndo(cur);
        _editPanel = next;
        RebuildEntities();
        CommandBox.Clear();
        ClearDyn();
        if (_editCmd == EditCmd.Copy)
        {
            // AutoCAD multiple-copy: base point and source selection stay; Enter ends.
            RefreshPrompt();
            CommandPrompt.Text = $"命令: 已复制 {touched.Count} 个特征 Δ({FormatSigned(dx)},{FormatSigned(dy)}) · 指定下一个第二点，或 Enter 结束:";
            Redraw();
            return;
        }
        EndEditCmd($"命令: 已移动 {touched.Count} 个特征 Δ({FormatSigned(dx)},{FormatSigned(dy)})");
    }

    static string FormatSigned(double v) =>
        (v > 0 ? "+" : "") + FormatDim2(v);

    static string FormatDim2(double v) =>
        v.ToString("0.##", CultureInfo.InvariantCulture);

    void EraseSelected()
    {
        if (_editPanel is not { } cur) return;
        if (_selIds.Count == 0)
        {
            RefreshPrompt();
            CommandPrompt.Text = "命令: ERASE 先选择要删除的特征";
            return;
        }
        var n = _selIds.Count;
        PushUndo(cur);
        _editPanel = PanelEdit.RemoveFeatures(cur, _selIds);
        _selIds.Clear();
        RebuildEntities();
        EndEditCmd($"命令: 已删除 {n} 个特征");
    }

    void SelectAllFeatures()
    {
        if (_editPanel is not { } cur) return;
        _selIds.Clear();
        foreach (var f in cur.Features) _selIds.Add(f.FeatureId);
        RefreshPrompt();
        Redraw();
    }

    void EditEscape()
    {
        if (_boxDragging)
        {
            _boxDragging = false;
            if (DraftHost.IsMouseCaptured) DraftHost.ReleaseMouseCapture();
        }
        // Same as 创建板件: Esc with a started LINE keeps it (Profile / 辅助线 write
        // immediately; Feature goes to the depth prompt). Only a lone first point cancels.
        if (_editPhase == EditPhase.DrawLine && _editLine.Count >= 2)
        {
            FinishEditLine();
            return;
        }
        if (_editCmd != EditCmd.None)
        {
            EndEditCmd("命令: *取消*");
            return;
        }
        if (_selIds.Count > 0)
        {
            _selIds.Clear();
            RefreshPrompt();
            Redraw();
            return;
        }
        RefreshPrompt();
    }

    void PushUndo(Panel snapshot)
    {
        _editUndo.Push(snapshot);
        _editRedo.Clear();
    }

    void EditUndo()
    {
        if (_editPanel is not { } cur) return;
        if (_editUndo.Count == 0)
        {
            RefreshPrompt();
            CommandPrompt.Text = "命令: 没有可撤销的操作";
            return;
        }
        _editRedo.Push(cur);
        _editPanel = _editUndo.Pop();
        RebuildEntities();
        EndEditCmd($"命令: 已撤销 · 还可撤销 {_editUndo.Count} 步");
    }

    void EditRedo()
    {
        if (_editPanel is not { } cur) return;
        if (_editRedo.Count == 0)
        {
            RefreshPrompt();
            CommandPrompt.Text = "命令: 没有可重做的操作";
            return;
        }
        _editUndo.Push(cur);
        _editPanel = _editRedo.Pop();
        RebuildEntities();
        EndEditCmd("命令: 已重做");
    }

    void EditCommit()
    {
        if (_editPanel is null) return;
        if (_editOriginal is not null)
        {
            UsageLog.LogEvent("ui", "panel.edit.commit", new Dictionary<string, object?>
            {
                ["panelId"] = _editPanel.PanelId,
                ["impact"] = PanelEdit.ClassifyChange(_editOriginal, _editPanel).ToString(),
                ["undoSteps"] = _editUndo.Count,
                ["featureCount"] = _editPanel.Features.Count,
                ["outlinePts"] = _editPanel.Outline.Points.Count,
            });
        }
        ResultPanel = _editPanel;
        Confirmed = true;
        DialogResult = true;
    }

    /// <summary>Window is closing without 写回 — ask before dropping real edits.</summary>
    bool EditConfirmDiscard()
    {
        if (!_editorOn || Confirmed || _editOriginal is null || _editPanel is null) return true;
        if (PanelEdit.ClassifyChange(_editOriginal, _editPanel) == EditImpact.None) return true;
        var ans = UiDialog.Show(this,
            $"有 {_editUndo.Count} 步改动还没写回方案。\n\n是 = 写回并关闭\n否 = 丢弃改动\n取消 = 继续编辑",
            "编辑板件", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (ans == MessageBoxResult.Cancel) return false;
        var impact = PanelEdit.ClassifyChange(_editOriginal, _editPanel).ToString();
        if (ans == MessageBoxResult.Yes)
        {
            UsageLog.LogEvent("ui", "panel.edit.commit", new Dictionary<string, object?>
            {
                ["panelId"] = _editPanel.PanelId,
                ["impact"] = impact,
                ["undoSteps"] = _editUndo.Count,
                ["featureCount"] = _editPanel.Features.Count,
                ["outlinePts"] = _editPanel.Outline.Points.Count,
                ["from"] = "close",
            });
            ResultPanel = _editPanel;
            Confirmed = true;
        }
        else
        {
            UsageLog.LogEvent("ui", "panel.edit.discard", new Dictionary<string, object?>
            {
                ["panelId"] = _editPanel.PanelId,
                ["impact"] = impact,
                ["undoSteps"] = _editUndo.Count,
                ["from"] = "close",
            });
        }
        return true;
    }

    // ---------------------------------------------------------------- toolbar

    void OnEditSelectClick(object sender, RoutedEventArgs e)
    {
        if (_editCmd != EditCmd.None) EndEditCmd();
        else { RefreshPrompt(); Redraw(); }
        CommandBox.Focus();
    }

    void OnEditMoveClick(object sender, RoutedEventArgs e) => StartEditCmd(EditCmd.Move);

    void OnEditCopyClick(object sender, RoutedEventArgs e) => StartEditCmd(EditCmd.Copy);

    void OnEditLineClick(object sender, RoutedEventArgs e) => StartEditCmd(EditCmd.Line);

    void OnEditTrimClick(object sender, RoutedEventArgs e) => StartEditCmd(EditCmd.Trim);

    void OnEditExtendClick(object sender, RoutedEventArgs e) => StartEditCmd(EditCmd.Extend);

    void OnEditEraseClick(object sender, RoutedEventArgs e)
    {
        EraseSelected();
        CommandBox.Focus();
    }

    void OnEditUndoClick(object sender, RoutedEventArgs e)
    {
        EditUndo();
        CommandBox.Focus();
    }

    void OnEditRedoClick(object sender, RoutedEventArgs e)
    {
        EditRedo();
        CommandBox.Focus();
    }

    void OnEditFitClick(object sender, RoutedEventArgs e)
    {
        _viewReady = false;
        Redraw();
        CommandBox.Focus();
    }

    // ---------------------------------------------------------------- keys

    /// <returns>true when the key was consumed by the editor.</returns>
    bool EditHandleKey(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var inBox = ReferenceEquals(e.OriginalSource, CommandBox);
        var boxHasText = !string.IsNullOrEmpty(CommandBox.Text);

        if (e.Key == Key.Escape)
        {
            if (inBox && boxHasText) { CommandBox.Clear(); return true; }
            EditEscape();
            return true;
        }
        if (ctrl && e.Key == Key.Z) { EditUndo(); return true; }
        if (ctrl && e.Key == Key.Y) { EditRedo(); return true; }
        if (ctrl && e.Key == Key.A) { SelectAllFeatures(); return true; }
        if (e.Key == Key.Delete && !(inBox && boxHasText))
        {
            EraseSelected();
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- mouse

    void EditOnLeftDown(MouseButtonEventArgs e)
    {
        var screen = ScreenFromMouse(e);
        var raw = ToWorld(screen.X, screen.Y);

        if (_editPhase is EditPhase.BasePoint or EditPhase.SecondPoint or EditPhase.DrawLine)
        {
            _cursor = raw;
            var resolved = ResolvePoint(raw, updateHover: true).Pt;
            EditAcceptPoint(resolved);
            CommandBox.Focus();
            return;
        }
        if (_editPhase == EditPhase.LineDepth)
        {
            RefreshPrompt();
            CommandBox.Focus();
            return;
        }

        var shift = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        var allowOutline = _editPhase == EditPhase.PickTargets && _editCmd == EditCmd.Trim;
        var hit = HitEntity(raw, allowOutline);

        if (_editPhase == EditPhase.PickTargets)
        {
            if (hit?.FeatureId is { } target && (hit.IsGroove || PanelEdit.IsOutlineId(target)))
                ApplyTrimExtend(target, raw);
            else if (hit is not null)
            {
                RefreshPrompt();
                CommandPrompt.Text = _editCmd == EditCmd.Trim
                    ? "命令: TRIM 点槽段或外框边 · 再点一个:"
                    : "命令: EXTEND 只能作用于槽 · 再点一个:";
            }
            CommandBox.Focus();
            return;
        }

        if (hit?.FeatureId is { } id)
        {
            if (shift)
            {
                if (!_selIds.Remove(id)) _selIds.Add(id);
            }
            else if (_editPhase is EditPhase.PickObjects or EditPhase.PickCutters)
            {
                _selIds.Add(id);
            }
            else
            {
                _selIds.Clear();
                _selIds.Add(id);
            }
            RefreshPrompt();
            Redraw();
            CommandBox.Focus();
            return;
        }

        _boxDragging = true;
        _boxStart = raw;
        _boxEnd = raw;
        _boxDownScreen = screen;
        DraftHost.CaptureMouse();
        Redraw();
    }

    void EditOnMove(WorldPt raw)
    {
        if (_boxDragging)
        {
            _boxEnd = raw;
            _hoverEnt = null;
            return;
        }
        _hoverEnt = _editPhase switch
        {
            EditPhase.Idle or EditPhase.PickObjects or EditPhase.PickCutters => HitEntity(raw),
            EditPhase.PickTargets when _editCmd == EditCmd.Trim =>
                HitEntity(raw, allowOutline: true) is { } t && (t.IsGroove || PanelEdit.IsOutlineId(t.FeatureId)) ? t : null,
            EditPhase.PickTargets => HitEntity(raw) is { IsGroove: true } g ? g : null,
            _ => null,
        };
    }

    void EditOnLeftUp(MouseButtonEventArgs e)
    {
        if (!_boxDragging) return;
        _boxDragging = false;
        if (DraftHost.IsMouseCaptured) DraftHost.ReleaseMouseCapture();
        var screen = ScreenFromMouse(e);
        var raw = ToWorld(screen.X, screen.Y);
        var moved = Math.Abs(screen.X - _boxDownScreen.X) + Math.Abs(screen.Y - _boxDownScreen.Y);
        var shift = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;

        if (moved < 4)
        {
            // Click on empty space.
            if (!shift && _editPhase == EditPhase.Idle && _selIds.Count > 0)
                _selIds.Clear();
            RefreshPrompt();
            Redraw();
            CommandBox.Focus();
            return;
        }

        _boxEnd = raw;
        var crossing = _boxEnd.X < _boxStart.X; // AutoCAD: right-to-left = crossing
        var minX = Math.Min(_boxStart.X, _boxEnd.X);
        var maxX = Math.Max(_boxStart.X, _boxEnd.X);
        var minY = Math.Min(_boxStart.Y, _boxEnd.Y);
        var maxY = Math.Max(_boxStart.Y, _boxEnd.Y);
        var picked = _entities
            .Where(en => en.Selectable && !PanelEdit.IsOutlineId(en.FeatureId)
                && (crossing ? BoxTouches(en, minX, minY, maxX, maxY) : BoxContains(en, minX, minY, maxX, maxY)))
            .Select(en => en.FeatureId!)
            .ToList();
        if (shift)
        {
            foreach (var id in picked)
                if (!_selIds.Remove(id)) _selIds.Add(id);
        }
        else
        {
            if (_editPhase == EditPhase.Idle) _selIds.Clear();
            foreach (var id in picked) _selIds.Add(id);
        }
        RefreshPrompt();
        Redraw();
        CommandBox.Focus();
    }

    static bool BoxContains(EditEntity en, double minX, double minY, double maxX, double maxY)
    {
        var (a, b, c, d) = en.Bounds();
        return a >= minX && c <= maxX && b >= minY && d <= maxY;
    }

    static bool BoxTouches(EditEntity en, double minX, double minY, double maxX, double maxY)
    {
        var (a, b, c, d) = en.Bounds();
        return a <= maxX && c >= minX && b <= maxY && d >= minY;
    }

    EditEntity? HitEntity(WorldPt w, bool allowOutline = false)
    {
        var tol = 6.0 / Math.Max(_view.Scale, 0.01f);
        EditEntity? best = null;
        var bestArea = double.MaxValue;
        foreach (var en in _entities)
        {
            if (!en.Selectable) continue;
            if (PanelEdit.IsOutlineId(en.FeatureId) && !allowOutline) continue;
            if (!HitTest(en, w, tol)) continue;
            var area = en.Area();
            if (area < bestArea)
            {
                bestArea = area;
                best = en;
            }
        }
        return best;
    }

    static bool HitTest(EditEntity en, WorldPt w, double tol)
    {
        if (en.IsCircle)
        {
            var dx = w.X - en.Center.X;
            var dy = w.Y - en.Center.Y;
            return Math.Sqrt(dx * dx + dy * dy) <= en.Radius + tol;
        }
        if (en.IsGroove)
            return DistToChain(w, en.Pts, closed: false) <= en.HalfWidthMm + tol;
        if (en.Closed)
        {
            if (PointInRing(w, en.Pts)) return true;
            return DistToChain(w, en.Pts, closed: true) <= tol;
        }
        return DistToChain(w, en.Pts, closed: false) <= tol;
    }

    static double DistToChain(WorldPt w, IReadOnlyList<WorldPt> pts, bool closed)
    {
        if (pts.Count == 0) return double.MaxValue;
        if (pts.Count == 1) return Math.Sqrt((w.X - pts[0].X) * (w.X - pts[0].X) + (w.Y - pts[0].Y) * (w.Y - pts[0].Y));
        var best = double.MaxValue;
        var n = pts.Count;
        for (var i = 1; i < n + (closed ? 1 : 0); i++)
        {
            var a = pts[i - 1];
            var b = pts[i % n];
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var len2 = vx * vx + vy * vy;
            var t = len2 < 1e-12 ? 0 : Math.Clamp(((w.X - a.X) * vx + (w.Y - a.Y) * vy) / len2, 0, 1);
            var px = a.X + t * vx - w.X;
            var py = a.Y + t * vy - w.Y;
            best = Math.Min(best, Math.Sqrt(px * px + py * py));
        }
        return best;
    }

    static bool PointInRing(WorldPt w, IReadOnlyList<WorldPt> pts)
    {
        var inside = false;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
        {
            var xi = pts[i].X;
            var yi = pts[i].Y;
            var xj = pts[j].X;
            var yj = pts[j].Y;
            var hit = yi > w.Y != yj > w.Y && w.X < (xj - xi) * (w.Y - yi) / (yj - yi + 1e-12) + xi;
            if (hit) inside = !inside;
        }
        return inside;
    }

    IEnumerable<SnapHit> EditSnapCandidates()
    {
        foreach (var en in _entities)
        {
            if (en.IsCircle)
            {
                yield return new SnapHit(en.Center, SnapKind.Center);
                yield return new SnapHit(new WorldPt(en.Center.X + en.Radius, en.Center.Y), SnapKind.Mid);
                yield return new SnapHit(new WorldPt(en.Center.X - en.Radius, en.Center.Y), SnapKind.Mid);
                yield return new SnapHit(new WorldPt(en.Center.X, en.Center.Y + en.Radius), SnapKind.Mid);
                yield return new SnapHit(new WorldPt(en.Center.X, en.Center.Y - en.Radius), SnapKind.Mid);
                continue;
            }
            var pts = en.Pts;
            for (var i = 0; i < pts.Count; i++)
            {
                if (i == pts.Count - 1 && en.Closed && pts.Count > 1 && NearPt(pts[0], pts[i])) break;
                yield return new SnapHit(pts[i], SnapKind.End);
                if (i == 0) continue;
                yield return new SnapHit(new WorldPt((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2), SnapKind.Mid);
            }
        }
        foreach (var chain in _chains.Where(c => c.Mode == PanelDraftMode.Guide))
        {
            var pts = chain.Pts;
            for (var i = 0; i < pts.Count; i++)
            {
                yield return new SnapHit(pts[i], SnapKind.End);
                if (i == 0) continue;
                yield return new SnapHit(new WorldPt((pts[i - 1].X + pts[i].X) / 2, (pts[i - 1].Y + pts[i].Y) / 2), SnapKind.Mid);
            }
        }
    }

    // ---------------------------------------------------------------- paint

    void FitEditorView(int w, int h)
    {
        if (_editPanel is not { } p || p.Outline.Points.Count == 0) return;
        var minX = p.Outline.Points.Min(q => q.X);
        var maxX = p.Outline.Points.Max(q => q.X);
        var minY = p.Outline.Points.Min(q => q.Y);
        var maxY = p.Outline.Points.Max(q => q.Y);
        var bw = Math.Max(1, maxX - minX);
        var bh = Math.Max(1, maxY - minY);
        var pad = OriginInset * (float)_dpiX;
        var availW = Math.Max(50, w - 2 * pad);
        var availH = Math.Max(50, h - 2 * pad);
        var scale = (float)Math.Min(availW / bw, availH / bh);
        _scale = Math.Clamp(scale, 0.08f, 64f);
        _ox = (float)(pad + (availW - bw * _scale) / 2 - minX * _scale);
        _oy = (float)(h - pad - (availH - bh * _scale) / 2 + minY * _scale);
    }

    void DrawEditor(SKCanvas canvas)
    {
        if (_editPanel is null) return;
        var outline = _entities.FirstOrDefault(en => PanelEdit.IsOutlineId(en.FeatureId));
        var cutouts = _entities.Where(en => en.IsCutout).ToList();

        // Board body with through cutouts as voids.
        if (outline is { Pts.Count: >= 3 })
        {
            using var fill = new SKPaint { Color = new SKColor(0x4A, 0x9A, 0xE8, 0x2A), IsAntialias = true };
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            AppendClosed(path, outline.Pts);
            foreach (var c in cutouts) AppendClosed(path, c.Pts);
            canvas.DrawPath(path, fill);
            using var ink = new SKPaint
            {
                Color = ProfileInk, StrokeWidth = 1.4f, IsStroke = true, IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round,
            };
            DrawChain(canvas, outline.Pts, ink);
        }

        var delta = _editPhase == EditPhase.SecondPoint ? LiveMoveDelta() : null;
        var cuttersShown = _editPhase == EditPhase.PickTargets;
        foreach (var en in _entities)
        {
            if (!en.Selectable) continue;
            var hovered = ReferenceEquals(en, _hoverEnt);
            if (PanelEdit.IsOutlineId(en.FeatureId))
            {
                if (hovered)
                    DrawEntity(canvas, en, false, true, ghost: false, 0, 0);
                continue;
            }
            var isCutter = cuttersShown && (_cutters?.Contains(en.FeatureId!) ?? false);
            var selected = _selIds.Contains(en.FeatureId!) || isCutter;
            DrawEntity(canvas, en, selected, hovered, ghost: false, 0, 0);
            if (selected && delta is { } d)
                DrawEntity(canvas, en, true, false, ghost: true, d.Dx, d.Dy);
        }

        foreach (var chain in _chains.Where(c => c.Mode == PanelDraftMode.Guide && c.Pts.Count >= 2))
        {
            using var ink = LayerPaint(PanelDraftMode.Guide, dashed: true);
            DrawChain(canvas, chain.Pts, ink);
        }

        DrawMoveRubber(canvas);
        DrawEditLineRubber(canvas);
        DrawSelectionBox(canvas);
    }

    (double Dx, double Dy)? LiveMoveDelta()
    {
        if (LiveMoveDest() is not { } dest || EditDynAnchor is not { } b)
            return null;
        return (dest.X - b.X, dest.Y - b.Y);
    }

    WorldPt? LiveMoveDest()
    {
        if (EditDynAnchor is not { } b)
            return null;
        var raw = _cursor ?? b;
        var dest = ResolvePoint(raw, updateHover: false).Pt;
        return new WorldPt(b.X + (_lockDx ?? dest.X - b.X), b.Y + (_lockDy ?? dest.Y - b.Y));
    }

    void DrawEditLineRubber(SKCanvas canvas)
    {
        if (_editPhase is not (EditPhase.DrawLine or EditPhase.LineDepth)) return;
        var pts = _editPhase == EditPhase.LineDepth && _editPendingPath is { } pend
            ? pend.Select(p => new WorldPt(p.X, p.Y)).ToList()
            : _editLine;
        if (pts.Count == 0) return;

        var layerInk = LayerColor(_mode);
        if (_mode == PanelDraftMode.Feature)
        {
            var halfW = Math.Max(0.5, (_lastGrooveWidth ?? 6) * 0.5) * _view.Scale;
            using var strip = new SKPaint
            {
                Color = layerInk.WithAlpha(0x50),
                StrokeWidth = (float)(halfW * 2),
                IsStroke = true,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Miter,
            };
            if (pts.Count >= 2) DrawChain(canvas, pts, strip);
        }
        using var center = new SKPaint
        {
            Color = layerInk,
            StrokeWidth = 1.3f,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            PathEffect = _mode == PanelDraftMode.Guide ? SKPathEffect.CreateDash([7, 4], 0) : null,
        };
        if (pts.Count >= 2)
            DrawChain(canvas, pts, center);
        foreach (var p in pts) DrawCenterMark(canvas, p);

        if (_editPhase != EditPhase.DrawLine || LiveMoveDest() is not { } dest) return;
        var last = pts[^1];
        using var dash = new SKPaint
        {
            Color = SelInk,
            StrokeWidth = 1.15f,
            IsStroke = true,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
        };
        var from = ToScreen(last.X, last.Y);
        var to = ToScreen(dest.X, dest.Y);
        canvas.DrawLine(from.X, from.Y, to.X, to.Y, dash);
        DrawMoveDynDims(canvas, last, dest);
        var len = Math.Sqrt((dest.X - last.X) * (dest.X - last.X) + (dest.Y - last.Y) * (dest.Y - last.Y));
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 11);
        using var ink = new SKPaint { Color = SelInk, IsAntialias = true };
        canvas.DrawText($"L {FormatDim2(len)}  Δ {FormatSigned(dest.X - last.X)}, {FormatSigned(dest.Y - last.Y)}", to.X + 12, to.Y + 18, SKTextAlign.Left, font, ink);
    }

    void DrawEntity(SKCanvas canvas, EditEntity en, bool selected, bool hovered, bool ghost, double dx, double dy)
    {
        var baseInk = en.IsCutout ? CutoutInk : FeatureInk;
        var color = ghost ? SelInk : selected ? SelInk : hovered ? HoverInk : baseInk;
        using var stroke = new SKPaint
        {
            Color = ghost ? color.WithAlpha(0xB0) : color,
            StrokeWidth = selected || hovered ? 2.0f : 1.15f,
            IsStroke = true,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            PathEffect = ghost ? SKPathEffect.CreateDash([6, 4], 0) : en.IsCutout ? SKPathEffect.CreateDash([9, 4], 0) : null,
        };
        using var fill = new SKPaint
        {
            Color = (selected ? SelInk : baseInk).WithAlpha((byte)(ghost ? 0x18 : selected ? 0x38 : 0x22)),
            IsAntialias = true,
        };

        IReadOnlyList<WorldPt> Shift(IReadOnlyList<WorldPt> pts) =>
            dx == 0 && dy == 0 ? pts : pts.Select(p => new WorldPt(p.X + dx, p.Y + dy)).ToList();

        if (en.IsCircle)
        {
            var (sx, sy) = ToScreen(en.Center.X + dx, en.Center.Y + dy);
            var r = (float)(en.Radius * _view.Scale);
            canvas.DrawCircle(sx, sy, r, fill);
            canvas.DrawCircle(sx, sy, r, stroke);
            const float cross = 4;
            canvas.DrawLine(sx - cross, sy, sx + cross, sy, stroke);
            canvas.DrawLine(sx, sy - cross, sx, sy + cross, stroke);
        }
        else if (en.IsGroove)
        {
            if (en.Strip is { Count: >= 3 } strip)
            {
                using var path = new SKPath();
                AppendClosed(path, Shift(strip));
                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, stroke);
            }
            DrawChain(canvas, Shift(en.Pts), stroke);
        }
        else if (en.Closed)
        {
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            AppendClosed(path, Shift(en.Pts));
            foreach (var isl in en.Islands) AppendClosed(path, Shift(isl));
            if (!en.IsCutout) canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
        }
        else
        {
            DrawChain(canvas, Shift(en.Pts), stroke);
        }

        if (ghost) return;

        if (selected)
            DrawGrips(canvas, en);

        // Label at anchor. Small holes only get one when selected / hovered / zoomed in,
        // otherwise a 40-hole side panel turns into a wall of ids.
        var showLabel = selected || hovered || !en.IsCircle || en.Radius * _view.Scale > 9;
        if (!showLabel) return;
        var anchor = en.IsCircle
            ? new WorldPt(en.Center.X + en.Radius, en.Center.Y + en.Radius)
            : en.Pts.Count > 0 ? en.Pts[0] : default;
        var (lx, ly) = ToScreen(anchor.X, anchor.Y);
        using var font = new SKFont(LabelTypeface, 10.5f);
        using var textInk = new SKPaint
        {
            Color = selected ? SelInk : hovered ? HoverInk : new SKColor(0x9A, 0x9A, 0x9A),
            IsAntialias = true,
        };
        canvas.DrawText(en.Label, lx + 5, ly - 5, SKTextAlign.Left, font, textInk);
    }

    void DrawGrips(SKCanvas canvas, EditEntity en)
    {
        using var grip = new SKPaint { Color = new SKColor(0x2A, 0x6A, 0xE8), IsAntialias = true };
        using var edge = new SKPaint { Color = SKColors.White, IsStroke = true, StrokeWidth = 1, IsAntialias = true };
        const float g = 3.5f;
        void Grip(WorldPt p)
        {
            var (sx, sy) = ToScreen(p.X, p.Y);
            canvas.DrawRect(sx - g, sy - g, g * 2, g * 2, grip);
            canvas.DrawRect(sx - g, sy - g, g * 2, g * 2, edge);
        }
        if (en.IsCircle)
        {
            Grip(en.Center);
            Grip(new WorldPt(en.Center.X + en.Radius, en.Center.Y));
            Grip(new WorldPt(en.Center.X - en.Radius, en.Center.Y));
            Grip(new WorldPt(en.Center.X, en.Center.Y + en.Radius));
            Grip(new WorldPt(en.Center.X, en.Center.Y - en.Radius));
            return;
        }
        var n = en.Pts.Count;
        if (en.Closed && n > 1 && NearPt(en.Pts[0], en.Pts[^1])) n--;
        if (n > 64)
        {
            // Dense arcs / circles-as-polylines: grips only at bbox corners.
            var (a, b, c, d) = en.Bounds();
            Grip(new WorldPt(a, b));
            Grip(new WorldPt(c, b));
            Grip(new WorldPt(c, d));
            Grip(new WorldPt(a, d));
            return;
        }
        for (var i = 0; i < n; i++) Grip(en.Pts[i]);
    }

    void DrawMoveRubber(SKCanvas canvas)
    {
        if (_editPhase != EditPhase.SecondPoint || _basePt is not { } b || _cursor is not { } raw)
        {
            if (_basePt is { } bp) DrawCenterMark(canvas, bp);
            return;
        }
        if (LiveMoveDest() is not { } dest) return;
        using var dash = new SKPaint
        {
            Color = SelInk,
            StrokeWidth = 1.15f,
            IsStroke = true,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
        };
        var from = ToScreen(b.X, b.Y);
        var to = ToScreen(dest.X, dest.Y);
        canvas.DrawLine(from.X, from.Y, to.X, to.Y, dash);
        DrawCenterMark(canvas, b);
        DrawMoveDynDims(canvas, b, dest);

        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), 11);
        using var ink = new SKPaint { Color = SelInk, IsAntialias = true };
        canvas.DrawText($"Δ {FormatSigned(dest.X - b.X)}, {FormatSigned(dest.Y - b.Y)}", to.X + 12, to.Y + 18, SKTextAlign.Left, font, ink);
    }

    void DrawMoveDynDims(SKCanvas canvas, WorldPt last, WorldPt dest)
    {
        var dx = dest.X - last.X;
        var dy = dest.Y - last.Y;
        var gap = 18f / Math.Max(_view.Scale, 0.01f);
        var yOff = dy >= 0 ? -gap : gap;
        var xOff = dx >= 0 ? gap : -gap;
        DrawCadDim(canvas, new WorldPt(last.X, last.Y + yOff), new WorldPt(dest.X, last.Y + yOff),
            FormatSigned(dx), horizontal: true,
            _dynEdit == DynField.X, _lockDx is not null);
        DrawCadDim(canvas, new WorldPt(dest.X + xOff, last.Y), new WorldPt(dest.X + xOff, dest.Y),
            FormatSigned(dy), horizontal: false,
            _dynEdit == DynField.Y, _lockDy is not null);
    }

    void DrawSelectionBox(SKCanvas canvas)
    {
        if (!_boxDragging) return;
        var a = ToScreen(_boxStart.X, _boxStart.Y);
        var b = ToScreen(_boxEnd.X, _boxEnd.Y);
        var crossing = _boxEnd.X < _boxStart.X;
        var rect = SKRect.Create(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        using var fill = new SKPaint
        {
            Color = crossing ? new SKColor(0x3C, 0xBF, 0x5A, 0x28) : new SKColor(0x4A, 0x9A, 0xE8, 0x28),
            IsAntialias = true,
        };
        using var stroke = new SKPaint
        {
            Color = crossing ? new SKColor(0x3C, 0xBF, 0x5A) : new SKColor(0x9A, 0xC8, 0xF0),
            StrokeWidth = 1,
            IsStroke = true,
            IsAntialias = true,
            PathEffect = crossing ? SKPathEffect.CreateDash([4, 3], 0) : null,
        };
        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);
    }
}
