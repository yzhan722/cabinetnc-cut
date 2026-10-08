using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CabinetNC.Infrastructure.Diagnostics;

namespace CabinetNC.Desktop;

public partial class MainWindow
{
    const int WmLButtonDown = 0x0201;

    static MainWindow? _probeOwner;
    static bool _tooltipClassHandlers;

    DependencyObject? _hoverTarget;
    long _hoverTicks;
    ToolTip? _openTip;
    long _tooltipOpenTicks;
    long _tooltipLeaveTicks;
    string? _tooltipName;
    long _wndButtonDownTicks;
    long _wpfButtonDownTicks;
    string? _clickName;

    void DisableTabletPressAndHold(IntPtr hwnd)
    {
        // The touchpad holds a press for about a second while it decides
        // whether this is a right-click. The window property opts this window out.
        const string name = "MicrosoftTabletPenServiceProperty";
        if (GlobalAddAtom(name) == 0) return;
        SetProp(hwnd, name, new IntPtr(1));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern ushort GlobalAddAtom(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    void AttachInputLagProbes()
    {
        _probeOwner = this;
        if (!_tooltipClassHandlers)
        {
            // ToolTipOpening is a direct event and never reaches the window; ToolTip.Opened does via class handlers.
            EventManager.RegisterClassHandler(typeof(ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler(OnAnyTooltipOpened));
            EventManager.RegisterClassHandler(typeof(ToolTip), System.Windows.Controls.ToolTip.ClosedEvent, new RoutedEventHandler(OnAnyTooltipClosed));
            _tooltipClassHandlers = true;
        }
        AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnProbeMouseMove), handledEventsToo: true);
        AddHandler(UIElement.MouseLeaveEvent, new MouseEventHandler((_, _) => LeaveHoverTarget(null)), handledEventsToo: true);
        AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnProbeMouseDown), handledEventsToo: true);
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnProbeClick), handledEventsToo: true);
    }

    void NoteMouseMessage(int msg)
    {
        if (msg == WmLButtonDown)
            _wndButtonDownTicks = Stopwatch.GetTimestamp();
    }

    void OnProbeMouseMove(object sender, MouseEventArgs e)
    {
        var target = HoverTarget(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(target, _hoverTarget)) return;
        LeaveHoverTarget(target);
        if (target is null) return;
        _hoverTicks = Stopwatch.GetTimestamp();
        var name = Describe(target);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            var paintMs = MsSince(_hoverTicks);
            if (paintMs < 20) return;
            UsageLog.LogEvent("ui", "hover.paint", new Dictionary<string, object?>
            {
                ["name"] = name,
                ["paintMs"] = paintMs,
            });
        });
    }

    void LeaveHoverTarget(DependencyObject? next)
    {
        _hoverTarget = next;
        if (_openTip is { IsOpen: true } tip && !OwnsTip(next, tip))
        {
            _tooltipLeaveTicks = Stopwatch.GetTimestamp();
            tip.IsOpen = false;
        }
    }

    static bool OwnsTip(DependencyObject? target, ToolTip tip) =>
        target is not null && ReferenceEquals(tip.PlacementTarget, target);

    static void OnAnyTooltipOpened(object sender, RoutedEventArgs e)
    {
        if (_probeOwner is not { } w || sender is not ToolTip tip) return;
        w._openTip = tip;
        w._tooltipOpenTicks = Stopwatch.GetTimestamp();
        w._tooltipLeaveTicks = 0;
        w._tooltipName = Describe(tip.PlacementTarget);
        UsageLog.LogEvent("ui", "hover.tooltip", new Dictionary<string, object?>
        {
            ["name"] = w._tooltipName,
            ["openMs"] = w._hoverTicks == 0 ? -1 : MsSince(w._hoverTicks),
        });
    }

    static void OnAnyTooltipClosed(object sender, RoutedEventArgs e)
    {
        if (_probeOwner is not { } w || w._tooltipOpenTicks == 0) return;
        UsageLog.LogEvent("ui", "hover.tooltip.hide", new Dictionary<string, object?>
        {
            ["name"] = w._tooltipName,
            ["visibleMs"] = MsSince(w._tooltipOpenTicks),
            ["afterLeaveMs"] = w._tooltipLeaveTicks == 0 ? -1 : MsSince(w._tooltipLeaveTicks),
        });
        w._tooltipOpenTicks = 0;
        w._tooltipLeaveTicks = 0;
        if (ReferenceEquals(w._openTip, sender)) w._openTip = null;
    }

    void OnProbeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _wpfButtonDownTicks = Stopwatch.GetTimestamp();
        _clickName = Describe(e.OriginalSource as DependencyObject);
    }

    void OnProbeClick(object sender, RoutedEventArgs e)
    {
        if (_wpfButtonDownTicks == 0) return;
        var name = _clickName ?? Describe(e.OriginalSource as DependencyObject);
        UsageLog.LogEvent("ui", "click.lag", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["downToClickMs"] = MsSince(_wpfButtonDownTicks),
            ["wndToClickMs"] = _wndButtonDownTicks == 0 ? -1 : MsSince(_wndButtonDownTicks),
        });
        _wpfButtonDownTicks = 0;
        _wndButtonDownTicks = 0;
    }

    static int MsSince(long start) =>
        (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    static DependencyObject? HoverTarget(DependencyObject? source)
    {
        DependencyObject? control = null;
        while (source is not null)
        {
            if (source is FrameworkElement { ToolTip: not null })
                return source;
            if (control is null && source is ButtonBase or TabItem or ListBoxItem)
                control = source;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return control;
    }

    static string Describe(DependencyObject? source)
    {
        var named = source;
        while (named is not null)
        {
            if (named is FrameworkElement fe)
            {
                var auto = System.Windows.Automation.AutomationProperties.GetName(fe);
                if (!string.IsNullOrWhiteSpace(auto)) return auto;
                if (fe is ContentControl { Content: string text } && !string.IsNullOrWhiteSpace(text))
                    return text.Replace("_", "", StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(fe.Name) && fe.Name != "Bd") return fe.Name;
            }
            if (named is ButtonBase or TabItem or ListBoxItem)
                return named.GetType().Name;
            named = named is Visual ? VisualTreeHelper.GetParent(named) : LogicalTreeHelper.GetParent(named);
        }
        return source?.GetType().Name ?? "";
    }
}
