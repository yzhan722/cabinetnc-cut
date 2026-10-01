using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CabinetNC.Compute.Contracts;
using CabinetNC.Domain;
using CabinetNC.Domain.Geometry;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using CabinetNC.FusionPackage;
using CabinetNC.Infrastructure.Diagnostics;
using CabinetNC.Infrastructure.Projects;
using PanelPart = CabinetNC.Domain.Parts.Panel;
using StatusKind = CabinetNC.Desktop.Core.StatusSeverity;

namespace CabinetNC.Desktop;

/// <summary>
/// 补板库 — per-project recut library.
///
/// Two lists travel with the project: panels that must be cut again (one copy each) and
/// remnants on hand (rectangle or a drawn ortho outline). "补切密排" parks the current plan (package + nest + ops +
/// export) as an in-memory <see cref="ProjectDocument"/>, swaps in the pending pieces and
/// nests them onto the remnants only; when something does not fit the operator is asked
/// before a full sheet is opened. "返回原方案" restores the parked document untouched.
/// </summary>
public partial class MainWindow
{
    sealed class RecutPendingItem
    {
        public required string Id { get; init; }
        public required string SourcePanelId { get; init; }
        public required PanelPart Panel { get; init; }
        public string? AddedAt { get; init; }
    }

    sealed class RecutRemnantItem
    {
        public required string Id { get; init; }
        public string? Material { get; init; }
        public double ThicknessMm { get; init; }
        public double WidthMm { get; init; }
        public double LengthMm { get; init; }
        public IReadOnlyList<Point2>? Outline { get; init; }
        /// <summary>manual | sheet | drawn</summary>
        public string Source { get; init; } = "manual";
        public string? Note { get; init; }
        public string? AddedAt { get; init; }

        public NestGroupKey Key => NestGroupKey.From(Material, ThicknessMm);
    }

    sealed class RecutSnapshot
    {
        public required ProjectDocument Doc { get; init; }
        public string? ProjectDbPath { get; init; }
        public string? SourcePath { get; init; }
        public string? SavedFingerprint { get; init; }
        public bool WasDirty { get; init; }
    }

    /// <summary>Row model for both library lists.</summary>
    sealed class RecutLibRow
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Size { get; init; }
        public required string Kind { get; init; }

        /// <summary>UIA / screen-reader name of the list item.</summary>
        public override string ToString() => $"{Title} · {Size} · {Kind}";
    }

    readonly List<RecutPendingItem> _recutPending = [];
    readonly List<RecutRemnantItem> _recutRemnants = [];
    /// <summary>Full sheets the operator agreed to open during this recut (per material kind).</summary>
    readonly List<NestSheetSpec> _recutExtraSheets = [];
    RecutSnapshot? _recutSnapshot;

    bool InRecutMode => _recutSnapshot is not null;

    // ----- session <-> memory -------------------------------------------------------

    List<RecutPendingDto> CaptureRecutPending() =>
        _recutPending.Select(p =>
        {
            var (w, h) = SizeOf(p.Panel);
            return new RecutPendingDto
            {
                Id = p.Id,
                SourcePanelId = p.SourcePanelId,
                PanelJson = SerializeSinglePanel(p.Panel),
                Title = p.Panel.DisplayTitle,
                Material = p.Panel.Material,
                ThicknessMm = p.Panel.ThicknessMm,
                WidthMm = w,
                LengthMm = h,
                AddedAt = p.AddedAt,
            };
        }).ToList();

    List<RecutRemnantDto> CaptureRecutRemnants() =>
        _recutRemnants.Select(r => new RecutRemnantDto
        {
            Id = r.Id,
            Material = r.Material,
            ThicknessMm = r.ThicknessMm,
            WidthMm = r.WidthMm,
            LengthMm = r.LengthMm,
            Outline = r.Outline is { Count: >= 4 }
                ? r.Outline.Select(p => new XyDto { X = p.X, Y = p.Y }).ToList()
                : null,
            Source = r.Source,
            Note = r.Note,
            AddedAt = r.AddedAt,
        }).ToList();

    void ApplyRecutLibrary(ProjectSessionState state)
    {
        _recutPending.Clear();
        foreach (var dto in state.RecutPending)
        {
            var panel = DeserializeSinglePanel(dto.PanelJson)
                        ?? _session.Package?.Panels.FirstOrDefault(p => p.PanelId == dto.SourcePanelId);
            if (panel is null) continue;
            _recutPending.Add(new RecutPendingItem
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? NextRecutId("RC") : dto.Id,
                SourcePanelId = string.IsNullOrWhiteSpace(dto.SourcePanelId) ? panel.PanelId : dto.SourcePanelId,
                Panel = panel,
                AddedAt = dto.AddedAt,
            });
        }
        _recutRemnants.Clear();
        foreach (var dto in state.RecutRemnants)
        {
            IReadOnlyList<Point2>? outline = null;
            var w = dto.WidthMm;
            var l = dto.LengthMm;
            if (dto.Outline is { Count: >= 4 })
            {
                var raw = dto.Outline.Select(p => new Point2(p.X, p.Y)).ToList();
                if (RemnantOutline.TryBuild(raw, out var shape, out _) && shape is not null)
                {
                    outline = shape.Outline;
                    w = shape.WidthMm;
                    l = shape.LengthMm;
                }
            }

            if (w <= 0 || l <= 0) continue;
            _recutRemnants.Add(new RecutRemnantItem
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? NextRecutId("REM") : dto.Id,
                Material = dto.Material,
                ThicknessMm = dto.ThicknessMm,
                WidthMm = w,
                LengthMm = l,
                Outline = outline,
                Source = string.IsNullOrWhiteSpace(dto.Source) ? "manual" : dto.Source,
                Note = dto.Note,
                AddedAt = dto.AddedAt,
            });
        }
        if (_module == "remnants") RefreshRecutLibraryUi();
    }

    void ClearRecutLibrary()
    {
        _recutPending.Clear();
        _recutRemnants.Clear();
        _recutExtraSheets.Clear();
        _recutSnapshot = null;
        UpdateRecutChrome();
    }

    static string SerializeSinglePanel(PanelPart panel) =>
        CutPackageJson.Serialize(new CutPackage
        {
            SchemaName = CutPackage.Schema,
            Panels = [panel],
        });

    static PanelPart? DeserializeSinglePanel(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var r = CutPackageImporter.FromJson(json);
            return r.Ok ? r.Package?.Panels.FirstOrDefault() : null;
        }
        catch
        {
            return null;
        }
    }

    string NextRecutId(string prefix)
    {
        var n = 1;
        string id;
        do { id = $"{prefix}-{n++}"; }
        while (_recutPending.Any(p => p.Id == id) || _recutRemnants.Any(r => r.Id == id));
        return id;
    }

    // ----- nest page context menu -------------------------------------------------

    void OnNestContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var show = InRecutMode ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in menu.Items)
        {
            if (item is FrameworkElement { Tag: "recut" } fe)
                fe.Visibility = show;
        }
    }

    void OnRecutAddPendingClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || InRecutMode) return;
        var ids = NestContextIds();
        if (ids.Count == 0) return;
        var added = 0;
        foreach (var id in ids)
        {
            var panel = _session.Package.Panels.FirstOrDefault(p => p.PanelId == id);
            if (panel is null) continue;
            _recutPending.Add(new RecutPendingItem
            {
                Id = NextRecutId("RC"),
                SourcePanelId = panel.PanelId,
                Panel = panel.WithQuantity(1),
                AddedAt = DateTimeOffset.Now.ToString("o"),
            });
            added++;
        }
        if (added == 0) return;
        UsageLog.LogEvent("ui", "recut.pending.add", new Dictionary<string, object?>
        {
            ["panelIds"] = ids,
            ["pendingCount"] = _recutPending.Count,
        });
        var first = _session.Package.Panels.First(p => p.PanelId == ids[0]);
        SetStatus($"已加入待补件 · {added} 件 · 补板库共 {_recutPending.Count} 件待补", StatusKind.Success);
        ShowToast(added == 1 ? $"{first.DisplayTitle} 已加入待补件" : $"{added} 件已加入待补件",
            "原密排不动。到「补板库」填余料后点「补切密排」。",
            StatusKind.Success, "打开补板库", () => OnModuleClick(ModRemnantsBtn, new RoutedEventArgs()));
        if (_module == "remnants") RefreshRecutLibraryUi();
    }

    void OnRecutAddSheetRemnantClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null || _nest is not { Ok: true } || InRecutMode) return;
        var sheet = _activeNestSheet;
        var (w, l, t) = SheetCamMetrics(sheet);
        if (w <= 0 || l <= 0)
        {
            SetStatus("当前大板尺寸无效，无法加入余料");
            return;
        }
        string? material = sheet < _nestSheetsUsed.Count ? _nestSheetsUsed[sheet].Material : null;
        if (string.IsNullOrWhiteSpace(material))
        {
            var onSheet = _nest.Placements.Where(p => p.SheetIndex == sheet)
                .Select(p => _session.Package.Panels.FirstOrDefault(x => x.PanelId == p.PanelId))
                .FirstOrDefault(p => p is not null);
            material = onSheet?.Material;
            if (onSheet is { ThicknessMm: > 0 }) t = onSheet.ThicknessMm;
        }
        var note = $"大板 {sheet + 1}";
        if (_recutRemnants.Any(r => r.Source == "sheet" && r.Note == note
                                     && Math.Abs(r.WidthMm - w) < 0.5 && Math.Abs(r.LengthMm - l) < 0.5))
        {
            SetStatus($"{note} 已经在余料里", StatusKind.Warning);
            return;
        }
        var item = new RecutRemnantItem
        {
            Id = NextRecutId("REM"),
            Material = material,
            ThicknessMm = t,
            WidthMm = w,
            LengthMm = l,
            Source = "sheet",
            Note = note,
            AddedAt = DateTimeOffset.Now.ToString("o"),
        };
        _recutRemnants.Add(item);
        UsageLog.LogEvent("ui", "recut.remnant.add", new Dictionary<string, object?>
        {
            ["source"] = "sheet",
            ["sheetIndex"] = sheet,
            ["w"] = w,
            ["l"] = l,
            ["t"] = t,
            ["material"] = material,
        });
        SetStatus($"{note} 已加入余料 · {w:0.#}×{l:0.#}×{t:0.#} · 补板库共 {_recutRemnants.Count} 块", StatusKind.Success);
        ShowToast($"{note} 已加入余料", $"{w:0.#}×{l:0.#} · {material ?? "材料未定"}。补切时先排这张。",
            StatusKind.Success, "打开补板库", () => OnModuleClick(ModRemnantsBtn, new RoutedEventArgs()));
        if (_module == "remnants") RefreshRecutLibraryUi();
    }

    // ----- 补板库 page -----------------------------------------------------------------

    void RefreshRecutLibraryUi()
    {
        if (RecutLibBody is null) return;
        var hasPackage = _session.Package is not null;
        RemnantsEmptyState.Visibility = hasPackage ? Visibility.Collapsed : Visibility.Visible;
        RecutLibBody.Visibility = hasPackage ? Visibility.Visible : Visibility.Collapsed;
        RecutActionRow.Visibility = hasPackage ? Visibility.Visible : Visibility.Collapsed;
        if (!hasPackage)
        {
            RecutLibMeta.Text = "";
            return;
        }

        var pendingRows = _recutPending.Select(p =>
        {
            var (w, h) = SizeOf(p.Panel);
            return new RecutLibRow
            {
                Id = p.Id,
                Title = p.Panel.DisplayTitle,
                Size = $"{w:0.#}×{h:0.#}×{p.Panel.ThicknessMm:0.#}",
                Kind = KindDisplayName(p.Panel),
            };
        }).ToList();
        var keepPending = RecutPendingList.SelectedItems.OfType<RecutLibRow>().Select(r => r.Id).ToHashSet();
        RecutPendingList.ItemsSource = pendingRows;
        foreach (var row in pendingRows.Where(r => keepPending.Contains(r.Id)))
            RecutPendingList.SelectedItems.Add(row);
        RecutPendingCount.Text = pendingRows.Count == 0 ? "" : $"{pendingRows.Count} 件";
        RecutPendingEmpty.Visibility = pendingRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var remnantRows = _recutRemnants.Select(r => new RecutLibRow
        {
            Id = r.Id,
            Title = RemnantTitle(r),
            Size = $"{r.WidthMm:0.#}×{r.LengthMm:0.#}×{r.ThicknessMm:0.#}",
            Kind = KindLabelFor(r.Key, r.Material),
        }).ToList();
        var keepRem = RecutRemnantList.SelectedItems.OfType<RecutLibRow>().Select(r => r.Id).ToHashSet();
        RecutRemnantList.ItemsSource = remnantRows;
        foreach (var row in remnantRows.Where(r => keepRem.Contains(r.Id)))
            RecutRemnantList.SelectedItems.Add(row);
        RecutRemnantCount.Text = remnantRows.Count == 0 ? "" : $"{remnantRows.Count} 块";
        RecutRemnantEmpty.Visibility = remnantRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshRecutMaterialChoices();

        RecutLibMeta.Text =
            $"工程 {_session.ResolvedProjectName} · 待补 {_recutPending.Count} 件 · 余料 {_recutRemnants.Count} 块" +
            (InRecutMode ? " · 补切模式" : "");
        RecutNestBtn.IsEnabled = _recutPending.Count > 0;
        RecutNestBtn.Content = InRecutMode ? "重新补切密排 →" : "补切密排 →";
        RecutExitBtn.Visibility = InRecutMode ? Visibility.Visible : Visibility.Collapsed;
        RecutActionHint.Text = InRecutMode
            ? "补切模式：生产加工里现在是待补件和余料。切完（或不切了）点「返回原方案」。"
            : _recutPending.Count == 0
                ? "先在密排页右键板件「加入待补件」。"
                : _recutRemnants.Count == 0
                    ? "没有余料也能补切：放不下时会问要不要开整板。"
                    : "先排余料，放不下再问要不要开整板；原方案会先暂存。";
    }

    void RefreshRecutMaterialChoices()
    {
        var kinds = ProjectMaterialKinds();
        var keep = RecutRemMaterialBox.SelectedValue is NestGroupKey k ? k : (NestGroupKey?)null;
        RecutRemMaterialBox.ItemsSource = kinds;
        if (kinds.Count == 0) return;
        if (keep is { } kk && kinds.Any(o => o.Key.Equals(kk)))
            RecutRemMaterialBox.SelectedValue = kk;
        else if (RecutRemMaterialBox.SelectedIndex < 0)
            RecutRemMaterialBox.SelectedIndex = 0;
    }

    /// <summary>Material kinds of the project (or of the parked original while in recut mode).</summary>
    List<MaterialKindOption> ProjectMaterialKinds()
    {
        IEnumerable<PanelPart> panels = _session.Package?.Panels ?? [];
        var fromPending = _recutPending.Select(p => p.Panel);
        var groups = panels.Concat(fromPending)
            .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
            .OrderBy(g => g.Key.Material, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.ThicknessMm)
            .ToList();
        var list = new List<MaterialKindOption>();
        foreach (var g in groups)
        {
            var kind = _stockKinds.FirstOrDefault(k => NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(g.Key));
            var label = kind is not null && !string.IsNullOrWhiteSpace(kind.Label)
                ? kind.Label.Trim()
                : g.First().MaterialGroupLabel;
            list.Add(new MaterialKindOption { Key = g.Key, Label = label, PanelCount = g.Count() });
        }
        // Remnants entered for a kind that is no longer in the project still need a label.
        foreach (var r in _recutRemnants)
        {
            if (list.Any(o => o.Key.Equals(r.Key))) continue;
            list.Add(new MaterialKindOption { Key = r.Key, Label = KindLabelFor(r.Key, r.Material), PanelCount = 0 });
        }
        return list;
    }

    string KindLabelFor(NestGroupKey key, string? rawMaterial)
    {
        var kind = _stockKinds.FirstOrDefault(k => NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(key));
        if (kind is not null && !string.IsNullOrWhiteSpace(kind.Label)) return kind.Label.Trim();
        var sample = _session.Package?.Panels.FirstOrDefault(p => NestGroupKey.From(p.Material, p.ThicknessMm).Equals(key))
                     ?? _recutPending.Select(p => p.Panel).FirstOrDefault(p => NestGroupKey.From(p.Material, p.ThicknessMm).Equals(key));
        if (sample is not null) return sample.MaterialGroupLabel;
        return string.IsNullOrWhiteSpace(rawMaterial) ? $"未定材料 · {key.ThicknessMm:0.##}mm" : key.ToString();
    }

    static string RemnantTitle(RecutRemnantItem r)
    {
        if (!string.IsNullOrWhiteSpace(r.Note))
            return r.Source == "sheet" ? $"{r.Note}（未切）" : r.Note!;
        if (r.Source == "sheet") return "整张大板（未切）";
        if (r.Outline is { Count: > 4 })
            return r.Outline.Count == 6 ? "手画 L" : "手画";
        return r.Source == "drawn" ? "手画" : "手填";
    }

    void OnRecutRemnantDrawClick(object sender, RoutedEventArgs e) => OpenRemnantDraft(null);

    void OnRecutRemnantListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecutRemnantList.SelectedItem is not RecutLibRow row) return;
        var item = _recutRemnants.FirstOrDefault(r => r.Id == row.Id);
        if (item is not null)
            OpenRemnantDraft(item);
    }

    void OpenRemnantDraft(RecutRemnantItem? edit)
    {
        if (_session.Package is null) return;
        var kinds = RecutDraftKinds();
        if (kinds.Count == 0)
        {
            SetStatus("这份工程还没有材料种类，无法画余料", StatusKind.Warning);
            return;
        }

        string? material;
        double thickness;
        if (edit is not null)
        {
            material = edit.Material;
            thickness = edit.ThicknessMm;
        }
        else if (RecutRemMaterialBox.SelectedValue is NestGroupKey selected)
        {
            material = selected.Material == "(unspecified)" ? null : selected.Material;
            thickness = selected.ThicknessMm;
        }
        else
        {
            var pendingKeys = _recutPending
                .Select(p => NestGroupKey.From(p.Panel.Material, p.Panel.ThicknessMm))
                .Distinct()
                .ToList();
            if (pendingKeys.Count == 1)
            {
                material = pendingKeys[0].Material == "(unspecified)" ? null : pendingKeys[0].Material;
                thickness = pendingKeys[0].ThicknessMm;
            }
            else
            {
                material = kinds[0].Material;
                thickness = kinds[0].ThicknessMm;
            }
        }

        var seed = edit?.Outline
                   ?? (edit is { WidthMm: > 0, LengthMm: > 0 }
                       ? RemnantOutline.Rectangle(edit.WidthMm, edit.LengthMm)
                       : null);

        var dlg = new PanelDraftWindow { Owner = this };
        dlg.PrepareRemnant(edit?.Id, edit?.Note, material, thickness, seed);
        dlg.SetStockKinds(kinds, material, thickness);
        if (dlg.ShowDialog() != true || dlg.ResultRemnant is null) return;

        var result = dlg.ResultRemnant;
        var item = new RecutRemnantItem
        {
            Id = edit?.Id ?? NextRecutId("REM"),
            Material = result.Material,
            ThicknessMm = result.ThicknessMm,
            WidthMm = result.WidthMm,
            LengthMm = result.LengthMm,
            Outline = result.Outline,
            Source = "drawn",
            Note = result.Note,
            AddedAt = edit?.AddedAt ?? DateTimeOffset.Now.ToString("o"),
        };
        if (edit is not null)
        {
            var idx = _recutRemnants.FindIndex(r => r.Id == edit.Id);
            if (idx >= 0) _recutRemnants[idx] = item;
            else _recutRemnants.Add(item);
        }
        else
            _recutRemnants.Add(item);

        UsageLog.LogEvent("ui", edit is null ? "recut.remnant.draw" : "recut.remnant.edit",
            new Dictionary<string, object?>
            {
                ["id"] = item.Id,
                ["w"] = item.WidthMm,
                ["l"] = item.LengthMm,
                ["t"] = item.ThicknessMm,
                ["material"] = item.Material,
                ["vertices"] = item.Outline?.Count ?? 0,
            });
        SetStatus(
            edit is null
                ? $"已画余料 {item.WidthMm:0.#}×{item.LengthMm:0.#} · {KindLabelFor(item.Key, item.Material)} · 共 {_recutRemnants.Count} 块"
                : $"已改余料 {item.WidthMm:0.#}×{item.LengthMm:0.#} · {KindLabelFor(item.Key, item.Material)}",
            StatusKind.Success);
        RefreshRecutLibraryUi();
    }

    List<DraftStockKind> RecutDraftKinds()
    {
        var fromProject = DraftStockKinds().ToList();
        if (fromProject.Count > 0) return fromProject;
        return ProjectMaterialKinds()
            .Select(o => new DraftStockKind(
                o.Key.Material == "(unspecified)" ? "" : o.Key.Material,
                o.Key.ThicknessMm,
                o.Label))
            .ToList();
    }

    void OnRecutRemnantAddClick(object sender, RoutedEventArgs e)
    {
        if (_session.Package is null) return;
        var w = ParseMm(RecutRemWBox.Text, 0);
        var l = ParseMm(RecutRemLBox.Text, 0);
        if (w <= 0 || l <= 0)
        {
            SetStatus("余料宽/长须 > 0", StatusKind.Warning);
            return;
        }
        if (RecutRemMaterialBox.SelectedValue is not NestGroupKey key)
        {
            SetStatus("先选余料的材料 / 厚度", StatusKind.Warning);
            return;
        }
        var rawMaterial = key.Material == "(unspecified)" ? null : key.Material;
        var item = new RecutRemnantItem
        {
            Id = NextRecutId("REM"),
            Material = rawMaterial,
            ThicknessMm = key.ThicknessMm,
            WidthMm = w,
            LengthMm = l,
            Source = "manual",
            AddedAt = DateTimeOffset.Now.ToString("o"),
        };
        _recutRemnants.Add(item);
        UsageLog.LogEvent("ui", "recut.remnant.add", new Dictionary<string, object?>
        {
            ["source"] = "manual",
            ["w"] = w,
            ["l"] = l,
            ["t"] = key.ThicknessMm,
            ["material"] = rawMaterial,
        });
        SetStatus($"已添加余料 {w:0.#}×{l:0.#} · {KindLabelFor(key, rawMaterial)} · 共 {_recutRemnants.Count} 块", StatusKind.Success);
        RefreshRecutLibraryUi();
    }

    void OnRecutRemnantRemoveClick(object sender, RoutedEventArgs e)
    {
        var ids = RecutRemnantList.SelectedItems.OfType<RecutLibRow>().Select(r => r.Id).ToHashSet();
        if (ids.Count == 0)
        {
            SetStatus("先选中要移除的余料");
            return;
        }
        var n = _recutRemnants.RemoveAll(r => ids.Contains(r.Id));
        UsageLog.LogEvent("ui", "recut.remnant.remove", new Dictionary<string, object?> { ["count"] = n });
        SetStatus($"已移除余料 {n} 块");
        RefreshRecutLibraryUi();
    }

    void OnRecutRemnantClearClick(object sender, RoutedEventArgs e)
    {
        if (_recutRemnants.Count == 0) return;
        if (MessageBox.Show(this, $"清空这份工程的 {_recutRemnants.Count} 块余料？", "清空余料",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var n = _recutRemnants.Count;
        _recutRemnants.Clear();
        UsageLog.LogEvent("ui", "recut.remnant.clear", new Dictionary<string, object?> { ["count"] = n });
        SetStatus("余料已清空");
        RefreshRecutLibraryUi();
    }

    void OnRecutPendingRemoveClick(object sender, RoutedEventArgs e)
    {
        var ids = RecutPendingList.SelectedItems.OfType<RecutLibRow>().Select(r => r.Id).ToHashSet();
        if (ids.Count == 0)
        {
            SetStatus("先选中要移除的待补件");
            return;
        }
        var n = _recutPending.RemoveAll(p => ids.Contains(p.Id));
        UsageLog.LogEvent("ui", "recut.pending.remove", new Dictionary<string, object?> { ["count"] = n });
        SetStatus($"已移除待补件 {n} 件");
        RefreshRecutLibraryUi();
    }

    void OnRecutPendingClearClick(object sender, RoutedEventArgs e)
    {
        if (_recutPending.Count == 0) return;
        if (MessageBox.Show(this, $"清空这份工程的 {_recutPending.Count} 件待补件？", "清空待补件",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var n = _recutPending.Count;
        _recutPending.Clear();
        UsageLog.LogEvent("ui", "recut.pending.clear", new Dictionary<string, object?> { ["count"] = n });
        SetStatus("待补件已清空");
        RefreshRecutLibraryUi();
    }

    // ----- recut mode ----------------------------------------------------------------------

    async void OnRecutNestClick(object sender, RoutedEventArgs e) => await StartRecutAsync();

    async void OnRecutRenestClick(object sender, RoutedEventArgs e) => await StartRecutAsync();

    void OnRecutExitClick(object sender, RoutedEventArgs e) => ExitRecutMode();

    async Task StartRecutAsync()
    {
        if (_session.Package is null)
        {
            SetStatus("请先载入方案");
            return;
        }
        if (_recutPending.Count == 0)
        {
            SetStatus("没有待补件 · 密排页右键板件「加入待补件」", StatusKind.Warning);
            return;
        }
        if (_nestBusy) return;

        if (!InRecutMode)
        {
            if (!EnterRecutMode())
                return;
        }
        else
        {
            // Re-run from the library again: pending list may have changed while in recut mode.
            _recutExtraSheets.Clear();
            RebuildRecutPackage();
        }
        await RunRecutNestAsync();
    }

    bool EnterRecutMode()
    {
        if (_session.Package is null || string.IsNullOrWhiteSpace(_session.PackageJson))
            return false;
        var name = _session.ResolvedProjectName;
        var snapshot = new RecutSnapshot
        {
            Doc = BuildProjectDocument(name),
            ProjectDbPath = _session.ProjectDbPath,
            SourcePath = _session.SourcePath,
            SavedFingerprint = _savedWorkFingerprint,
            WasDirty = HasUnsavedWork(),
        };
        _recutSnapshot = snapshot;
        _recutExtraSheets.Clear();
        UsageLog.LogEvent("ui", "recut.enter", new Dictionary<string, object?>
        {
            ["project"] = name,
            ["pending"] = _recutPending.Count,
            ["remnants"] = _recutRemnants.Count,
        });
        RebuildRecutPackage();
        return true;
    }

    /// <summary>Swap the session package for the pending pieces (one copy each, unique ids).</summary>
    void RebuildRecutPackage()
    {
        if (_recutSnapshot is null) return;
        var original = CutPackageImporter.FromJson(_recutSnapshot.Doc.PackageJson).Package;
        var basePkg = original ?? _session.Package!;
        var originalName = _recutSnapshot.Doc.Name;
        var panels = new List<PanelPart>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _recutPending)
        {
            var id = p.Panel.PanelId;
            var n = 1;
            while (!seen.Add(id))
                id = $"{p.Panel.PanelId}~R{++n}";
            var copy = id == p.Panel.PanelId ? p.Panel.WithQuantity(1) : PanelEdit.Duplicate(p.Panel, id).WithQuantity(1);
            panels.Add(copy);
        }
        var pkg = basePkg.WithPanels(panels).WithJobId((basePkg.JobId ?? originalName) + "-recut");
        _session.AcceptPackage(pkg, _recutSnapshot.SourcePath);
        _session.ProjectName = originalName + " 补切";
        ClearManufacturingState(keepRecutLibrary: true);
        _module = "production";
        HighlightModule();
        ApplyModuleVisibility();
        BindPackage();
        UpdateRecutChrome();
    }

    void ExitRecutMode()
    {
        var snap = _recutSnapshot;
        if (snap is null) return;
        if (_nestBusy)
        {
            SetStatus("密排进行中，先取消再返回原方案", StatusKind.Warning);
            return;
        }
        // Library edits made while in recut mode win over the parked copy.
        var pending = _recutPending.ToList();
        var remnants = _recutRemnants.ToList();

        var result = _session.OpenPackageJson(snap.Doc.PackageJson, snap.SourcePath, snap.Doc.SourceSnapshotJson);
        if (!result.Ok)
        {
            SetStatus("返回原方案失败: " + string.Join("; ", result.Errors.Select(x => x.Message)), StatusKind.Error);
            return;
        }
        _recutSnapshot = null;
        _recutExtraSheets.Clear();
        ClearManufacturingState(keepRecutLibrary: true);
        ApplyProjectDocument(snap.Doc, snap.ProjectDbPath, snap.SourcePath);

        _recutPending.Clear();
        _recutPending.AddRange(pending);
        _recutRemnants.Clear();
        _recutRemnants.AddRange(remnants);

        _savedWorkFingerprint = snap.SavedFingerprint;
        ApplyProjectNameChrome();
        UpdateRecutChrome();
        RefreshWorkflowDots();
        UsageLog.LogEvent("ui", "recut.exit", new Dictionary<string, object?>
        {
            ["project"] = _session.ResolvedProjectName,
        });
        SetStatus($"已返回原方案 {_session.ResolvedProjectName}", StatusKind.Success);
    }

    /// <summary>Ctrl+S while recutting: the recut nest is never written; the parked original is.</summary>
    bool ConfirmLeaveRecutForSave()
    {
        var r = MessageBox.Show(this,
            "补切模式下的密排不会保存到工程。\n\n返回原方案再保存吗？（待补件和余料会一起保存）",
            "保存工程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (r != MessageBoxResult.Yes) return false;
        ExitRecutMode();
        return !InRecutMode;
    }

    void UpdateRecutChrome()
    {
        if (RecutBanner is null) return;
        RecutBanner.Visibility = InRecutMode ? Visibility.Visible : Visibility.Collapsed;
        if (InRecutMode)
        {
            RecutBannerText.Text =
                $"补切模式 · {_session.ResolvedProjectName} · 待补 {_recutPending.Count} 件 · 余料 {_recutRemnants.Count} 块" +
                (_recutExtraSheets.Count > 0 ? $" · 已加整板 {_recutExtraSheets.Count} 张" : "") +
                " · 原方案已暂存，这里的密排 / 刀路 / 导出只含补切";
        }
        if (_module == "remnants") RefreshRecutLibraryUi();
    }

    // ----- finite-stock nest -----------------------------------------------------------

    List<NestSheetSpec> BuildRecutStock()
    {
        var stock = new List<NestSheetSpec>();
        foreach (var r in _recutRemnants)
        {
            var kind = _stockKinds.FirstOrDefault(k => NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(r.Key));
            stock.Add(new NestSheetSpec
            {
                WidthMm = r.WidthMm,
                LengthMm = r.LengthMm,
                BorderMm = kind?.BorderMm ?? _library.Nest.BorderMm,
                SpacingMm = kind?.SpacingMm ?? _library.Nest.SpacingMm,
                AllowRotation = kind?.AllowRotate90 ?? _library.Nest.AllowRotation,
                AllowPartsInPart = false,
                Label = $"余料 {r.Id} {r.WidthMm:0.#}×{r.LengthMm:0.#}",
                Material = r.Key.Material,
                ThicknessMm = r.Key.ThicknessMm,
                SheetGrain = kind?.SheetGrain ?? SheetGrainKind.None,
                Blocked = r.Outline is { Count: >= 4 }
                          && RemnantOutline.TryBuild(r.Outline, out var shape, out _)
                          && shape is not null
                    ? shape.Blocked
                    : [],
            });
        }
        stock.AddRange(_recutExtraSheets);
        return stock;
    }

    NestSheetSpec FullSheetFor(NestGroupKey key)
    {
        var kind = _stockKinds.FirstOrDefault(k => NestGroupKey.From(k.MaterialId, k.ThicknessMm).Equals(key));
        var pkgSheet = _session.Package?.Sheets.FirstOrDefault(s => NestGroupKey.From(s.Material, s.ThicknessMm).Equals(key));
        var w = kind?.WidthMm ?? (pkgSheet is { WidthMm: > 0 } ? pkgSheet.WidthMm : _library.Nest.DefaultSheetWidthMm);
        var l = kind?.LengthMm ?? (pkgSheet is { LengthMm: > 0 } ? pkgSheet.LengthMm : _library.Nest.DefaultSheetLengthMm);
        var n = _recutExtraSheets.Count(s => NestGroupKey.From(s.Material, s.ThicknessMm).Equals(key)) + 1;
        return new NestSheetSpec
        {
            WidthMm = w,
            LengthMm = l,
            BorderMm = kind?.BorderMm ?? _library.Nest.BorderMm,
            SpacingMm = kind?.SpacingMm ?? _library.Nest.SpacingMm,
            AllowRotation = kind?.AllowRotate90 ?? _library.Nest.AllowRotation,
            AllowPartsInPart = false,
            Label = $"整板 {n} {w:0.#}×{l:0.#}",
            Material = key.Material,
            ThicknessMm = key.ThicknessMm,
            SheetGrain = kind?.SheetGrain ?? SheetGrainKind.None,
        };
    }

    async Task RunRecutNestAsync()
    {
        if (_nestBusy || _session.Package is null || !InRecutMode) return;
        _nestBusy = true;
        SetNestBusyUi(true);
        BeginNestProgress("补切密排 · 先排余料…");
        UsageLog.LogActionStart("recut.nest", new Dictionary<string, object?>
        {
            ["pending"] = _session.Package.Panels.Count,
            ["remnants"] = _recutRemnants.Count,
            ["extraSheets"] = _recutExtraSheets.Count,
        });
        try
        {
            RefreshStockMaterialCards();
            for (var round = 0; round < 8; round++)
            {
                var panels = _session.Package.Panels.ToList();
                var stock = BuildRecutStock();
                var allowRot = _stockKinds.Count > 0 ? _stockKinds.Any(k => k.AllowRotate90) : _library.Nest.AllowRotation;
                var border = _stockKinds.Count > 0 ? _stockKinds.Max(k => k.BorderMm) : _library.Nest.BorderMm;
                var spacing = _stockKinds.Count > 0 ? _stockKinds.Min(k => k.SpacingMm) : _library.Nest.SpacingMm;
                var settings = new NestSettings
                {
                    MarginMm = border,
                    ClearanceMm = spacing,
                    AllowRotation = allowRot,
                    GrainLock = true,
                };
                var packed = await Task.Run(() => RecutNester.Pack(panels, stock, settings, SizeOf)).ConfigureAwait(true);
                ApplyRecutNestResult(packed, stock);

                if (packed.Unplaced.Count == 0)
                    break;

                // Ask before opening a full sheet — that is the whole point of the recut library.
                var missing = packed.UnplacedReasons
                    .Select(r => panels.FirstOrDefault(p => p.PanelId == r.PanelId))
                    .OfType<PanelPart>()
                    .GroupBy(p => NestGroupKey.From(p.Material, p.ThicknessMm))
                    .ToList();
                var lines = missing.Select(g =>
                {
                    var full = FullSheetFor(g.Key);
                    return $"  {KindLabelFor(g.Key, g.First().Material)}：{g.Count()} 件 → 整板 {full.WidthMm:0.#}×{full.LengthMm:0.#}";
                });
                var ask = MessageBox.Show(this,
                    $"还有 {packed.Unplaced.Count} 件放不下现有余料：\n\n{string.Join("\n", lines)}\n\n要各加一张整板再排吗？",
                    "余料不够",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);
                if (ask != MessageBoxResult.Yes)
                    break;

                var before = packed.Unplaced.Count;
                foreach (var g in missing)
                    _recutExtraSheets.Add(FullSheetFor(g.Key));
                UsageLog.LogEvent("ui", "recut.addFullSheet", new Dictionary<string, object?>
                {
                    ["kinds"] = missing.Select(g => g.Key.ToString()).ToList(),
                    ["unplaced"] = before,
                });
                // A part larger than a full sheet would loop forever; the next round's result
                // is compared and the loop stops when adding sheets no longer helps.
                var check = RecutNester.Pack(panels, BuildRecutStock(), settings, SizeOf);
                if (check.Unplaced.Count >= before)
                {
                    ApplyRecutNestResult(check, BuildRecutStock());
                    ShowToast("整板也放不下", "剩下的板件比整板还大，或材料没有对应板材。请核对尺寸。", StatusKind.Warning);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("补切密排失败: " + ex.Message, StatusKind.Error);
            UsageLog.LogActionResult("recut.nest", new Dictionary<string, object?>(), error: ex.Message);
        }
        finally
        {
            EndNestProgress();
            SetNestBusyUi(false);
            _nestBusy = false;
            UpdateRecutChrome();
        }
    }

    void ApplyRecutNestResult(NestResult packed, IReadOnlyList<NestSheetSpec> stock)
    {
        _nestSheetsUsed = packed.SheetsUsed.ToList();
        _guillotineBySheet.Clear();
        _nestHolding.Clear();
        _holdingLayout = [];
        _holdingRegions = [];
        _partInPartSlots = [];
        _activeNestSheet = 0;
        ResetProfileBridges();
        _locked.Clear();

        _nest = new StartNestingReply
        {
            Ok = true,
            Engine = packed.Engine,
            SheetCount = packed.SheetCount,
        };
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
        _nest.Unplaced.AddRange(packed.Unplaced);
        var remnantSheets = _nestSheetsUsed.Count(s => s.Label?.StartsWith("余料", StringComparison.Ordinal) == true);
        var fullSheets = _nestSheetsUsed.Count - remnantSheets;
        _nest.Warnings.Add(new NestWarningMsg
        {
            Code = "engine",
            Message = $"补切 · 余料 {remnantSheets} 块 · 整板 {fullSheets} 张 · 库存 {stock.Count} 块可用",
        });
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
                Message = $"{g.Key}: placed {g.PlacedCount}/{g.PartCount} · sheets {g.SheetCount} · util {g.UtilizationPct:0.0}%",
            });
        }

        _showNest = true;
        ResetSimView();
        if (_stage != "nest" && _stage != "ops")
        {
            _stageChanging = true;
            StageTabs.SelectedIndex = 2;
            _stage = "nest";
            _stageChanging = false;
        }
        ApplyStageVisibility();
        UpdateStageChrome();
        BindPartList(_nest.Placements.FirstOrDefault()?.PanelId);
        UpdateCanvasHint();
        RebuildOpsOverlay();
        _session.MarkManufacturingClean();
        RefreshNestReport();
        RefreshWorkflowDots();
        UpdateNestSheetChrome();
        CanvasHost.InvalidateVisual();
        MarkWorkSaved();

        var placed = _nest.Placements.Count;
        SetStatus(
            $"补切密排 · 已排 {placed} 件 · 余料 {remnantSheets} 块 · 整板 {fullSheets} 张 · 未排 {_nest.Unplaced.Count}",
            _nest.Unplaced.Count > 0 ? StatusKind.Warning : StatusKind.Success);
        if (_nest.Unplaced.Count == 0 && placed > 0)
        {
            ShowToast($"补切密排完成 · {placed} 件",
                fullSheets == 0 ? "全部排在余料上。可以去计算刀路。" : $"用了 {remnantSheets} 块余料 + {fullSheets} 张整板。",
                StatusKind.Success, "去计算刀路", () => GoToStage("ops"));
        }
        UsageLog.LogActionResult("recut.nest", new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["placed"] = placed,
            ["unplaced"] = _nest.Unplaced.Count,
            ["remnantSheets"] = remnantSheets,
            ["fullSheets"] = fullSheets,
        });
    }
}
