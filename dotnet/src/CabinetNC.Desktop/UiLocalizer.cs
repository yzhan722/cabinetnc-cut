using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CabinetNC.Desktop.Core;
using SkiaSharp.Views.WPF;

namespace CabinetNC.Desktop;

/// <summary>
/// Applies <see cref="UiText"/> to open windows. Source strings stay Chinese;
/// English is written onto the controls and restored when the UI switches back.
/// </summary>
public static class UiLocalizer
{
    static readonly DependencyProperty SlotProperty = DependencyProperty.RegisterAttached(
        "Slot", typeof(Slot), typeof(UiLocalizer));

    static DispatcherTimer? _timer;
    static bool _applying;
    static bool _sweepLogged;
    static int _started;

    sealed class Slot
    {
        public Dictionary<DependencyProperty, string>? Source;
        public Dictionary<DependencyProperty, string>? Applied;
        public Dictionary<DependencyProperty, string>? Formats;
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        UiText.Changed += () => ApplyAll(refreshBindings: true);
        Load();
        // Code still writes Chinese into loaded controls at run time, so a slow idle sweep catches those.
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (UiText.English) Sweep();
        };
        _timer.Start();
        // Loaded is raised on every element, so each control translates itself once when it appears.
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (!UiText.English || _applying || sender is not FrameworkElement fe) return;
                if (fe.Name == "LangBtn") return;
                ApplyLoaded(fe);
            }));
    }

    static void ApplyLoaded(FrameworkElement fe)
    {
        try
        {
            ApplyNode(fe, refreshBindings: false);
            if (fe is ListView { View: GridView view })
            {
                foreach (var column in view.Columns)
                    ApplyNode(column, refreshBindings: false);
            }
        }
        catch
        {
            /* one control must not block the rest of the window */
        }
    }

    static void Sweep()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var visited = 0;
        if (_applying) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;
        _applying = true;
        try
        {
            foreach (Window window in app.Windows)
            {
                if (!window.IsVisible) continue;
                visited += Visit(window, refreshBindings: false);
            }
        }
        finally
        {
            _applying = false;
        }
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms >= 30 || !_sweepLogged)
        {
            _sweepLogged = true;
            Infrastructure.Diagnostics.UsageLog.LogEvent("perf", "i18n.sweep", new Dictionary<string, object?>
            {
                ["ms"] = Math.Round(ms, 1),
                ["nodes"] = visited,
            });
        }
    }

    public static void Toggle()
    {
        UiText.SetEnglish(!UiText.English);
        try
        {
            var path = PrefPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, UiText.English ? "en" : "zh");
        }
        catch
        {
            /* the switch still applies for this session */
        }
    }

    public static void Apply(DependencyObject root, bool refreshBindings = true) =>
        ApplyAll(refreshBindings, root);

    static void Load()
    {
        try
        {
            var path = PrefPath();
            if (System.IO.File.Exists(path) && System.IO.File.ReadAllText(path).Trim().Equals("en", StringComparison.OrdinalIgnoreCase))
                UiText.SetEnglish(true);
        }
        catch
        {
            /* default Chinese */
        }
    }

    static string PrefPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CabinetNC",
            "language.txt");

    static void ApplyAll(bool refreshBindings, DependencyObject? only = null)
    {
        if (_applying) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;
        _applying = true;
        try
        {
            if (only is not null)
            {
                Visit(only, refreshBindings);
                return;
            }
            foreach (Window window in app.Windows)
                Visit(window, refreshBindings);
        }
        finally
        {
            _applying = false;
        }
    }

    static int Visit(DependencyObject root, bool refreshBindings)
    {
        var nodes = new List<DependencyObject>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Collect(root, nodes, seen);
        foreach (var node in nodes)
        {
            try
            {
                ApplyNode(node, refreshBindings);
            }
            catch
            {
                /* one control must not block the rest of the window */
            }
        }
        return nodes.Count;
    }

    // A panel's children are both logical and visual children; without the seen set every
    // nesting level doubled the walk.
    static void Collect(DependencyObject root, List<DependencyObject> nodes, HashSet<DependencyObject> seen)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            if (node is FrameworkElement fe && fe.Name == "LangBtn") continue;
            nodes.Add(node);
            if (node is FrameworkElement el && el.ContextMenu is { } menu)
                stack.Push(menu);
            if (node is ListView list && list.View is GridView view)
            {
                foreach (var column in view.Columns)
                    stack.Push(column);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject logical) stack.Push(logical);
            }
            if (node is not Visual) continue;
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    static void ApplyNode(DependencyObject node, bool refreshBindings)
    {
        switch (node)
        {
            case Window window:
                ApplyString(window, Window.TitleProperty);
                break;
            case TextBlock block:
                ApplyTextBlock(block, refreshBindings);
                break;
            case AccessText access:
                ApplyString(access, AccessText.TextProperty);
                break;
            case TextBox box when box.IsReadOnly:
                ApplyString(box, TextBox.TextProperty);
                break;
            case ContentControl control:
                ApplyString(control, ContentControl.ContentProperty);
                break;
            case GridViewColumn column:
                ApplyString(column, GridViewColumn.HeaderProperty);
                break;
        }
        if (node is HeaderedItemsControl items)
            ApplyString(items, HeaderedItemsControl.HeaderProperty);
        else if (node is HeaderedContentControl header)
            ApplyString(header, HeaderedContentControl.HeaderProperty);
        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string)
                ApplyString(element, FrameworkElement.ToolTipProperty);
            var auto = System.Windows.Automation.AutomationProperties.GetName(element);
            if (!string.IsNullOrEmpty(auto))
                ApplyString(element, System.Windows.Automation.AutomationProperties.NameProperty);
        }
        if (refreshBindings && node is SKElement sk) sk.InvalidateVisual();
        if (node is not FrameworkElement bound) return;
        WrapBinding(bound, TextBlock.TextProperty, refreshBindings);
        WrapMulti(bound, refreshBindings);
    }

    static void ApplyTextBlock(TextBlock block, bool refreshBindings)
    {
        if (BindingOperations.GetBinding(block, TextBlock.TextProperty) is not null
            || BindingOperations.GetMultiBinding(block, TextBlock.TextProperty) is not null)
        {
            WrapBinding(block, TextBlock.TextProperty, refreshBindings);
            WrapMulti(block, refreshBindings);
            return;
        }
        ApplyString(block, TextBlock.TextProperty);
    }

    static void ApplyString(DependencyObject obj, DependencyProperty property)
    {
        if (obj.GetValue(property) is not string current) return;
        var slot = SlotOf(obj);
        slot.Source ??= new Dictionary<DependencyProperty, string>();
        slot.Applied ??= new Dictionary<DependencyProperty, string>();
        slot.Source.TryGetValue(property, out var source);
        slot.Applied.TryGetValue(property, out var applied);

        if (UiText.English)
        {
            if (!UiText.HasCjk(current)) return;
            slot.Source[property] = current;
            var english = UiText.ToEnglish(current);
            slot.Applied[property] = english;
            if (!string.Equals(english, current, StringComparison.Ordinal))
                obj.SetValue(property, english);
            return;
        }

        if (source is not null && current == applied && !string.Equals(current, source, StringComparison.Ordinal))
        {
            slot.Applied[property] = source;
            obj.SetValue(property, source);
        }
        else if (UiText.HasCjk(current))
        {
            slot.Source[property] = current;
            slot.Applied[property] = current;
        }
    }

    static void WrapBinding(FrameworkElement element, DependencyProperty property, bool refreshBindings)
    {
        var binding = BindingOperations.GetBinding(element, property);
        if (binding is null) return;
        if (binding.Mode is BindingMode.TwoWay or BindingMode.OneWayToSource) return;
        if (binding.Converter is UiTextConverter)
        {
            if (refreshBindings)
                BindingOperations.GetBindingExpression(element, property)?.UpdateTarget();
            return;
        }
        var next = new Binding
        {
            Path = binding.Path,
            Mode = binding.Mode == BindingMode.Default ? BindingMode.OneWay : binding.Mode,
            Converter = UiTextConverter.Instance,
            UpdateSourceTrigger = binding.UpdateSourceTrigger,
            StringFormat = FormatOf(element, property, binding.StringFormat),
        };
        if (binding.Source is not null) next.Source = binding.Source;
        if (binding.RelativeSource is not null) next.RelativeSource = binding.RelativeSource;
        if (!string.IsNullOrEmpty(binding.ElementName)) next.ElementName = binding.ElementName;
        element.SetBinding(property, next);
    }

    static readonly DependencyProperty[] MultiTargets = [TextBlock.TextProperty, ContentControl.ContentProperty];

    static void WrapMulti(FrameworkElement element, bool refreshBindings)
    {
        foreach (var property in MultiTargets)
        {
            var multi = BindingOperations.GetMultiBinding(element, property);
            if (multi is null || string.IsNullOrEmpty(multi.StringFormat)) continue;
            var slot = SlotOf(element);
            slot.Formats ??= new Dictionary<DependencyProperty, string>();
            if (!slot.Formats.ContainsKey(property))
                slot.Formats[property] = multi.StringFormat;
            var original = slot.Formats[property];
            var format = UiText.English ? UiText.ToEnglish(original) : original;
            if (!string.Equals(multi.StringFormat, format, StringComparison.Ordinal))
            {
                multi.StringFormat = format;
                BindingOperations.GetMultiBindingExpression(element, property)?.UpdateTarget();
            }
            else if (refreshBindings)
            {
                BindingOperations.GetMultiBindingExpression(element, property)?.UpdateTarget();
            }
        }
    }

    static string? FormatOf(FrameworkElement element, DependencyProperty property, string? format)
    {
        if (string.IsNullOrEmpty(format) || !UiText.HasCjk(format)) return format;
        var slot = SlotOf(element);
        slot.Formats ??= new Dictionary<DependencyProperty, string>();
        if (!slot.Formats.ContainsKey(property))
            slot.Formats[property] = format;
        var original = slot.Formats[property];
        return UiText.English ? UiText.ToEnglish(original) : original;
    }

    static Slot SlotOf(DependencyObject obj)
    {
        if (obj.GetValue(SlotProperty) is Slot slot) return slot;
        slot = new Slot();
        obj.SetValue(SlotProperty, slot);
        return slot;
    }
}

public sealed class UiTextConverter : IValueConverter
{
    public static readonly UiTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string text ? UiText.T(text) : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
