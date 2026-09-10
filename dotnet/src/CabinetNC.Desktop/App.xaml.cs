using System.Windows.Threading;
using CabinetNC.Infrastructure.Diagnostics;

namespace CabinetNC.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        UsageLog.LogEvent(
            "app_start",
            "desktop.start",
            new Dictionary<string, object?>
            {
                ["baseDirectory"] = AppContext.BaseDirectory,
                ["logDirs"] = UsageLog.LogDirs().ToList(),
                ["args"] = e.Args.Take(20).ToList(),
            });

        base.OnStartup(e);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        UsageLog.LogEvent(
            "app_exit",
            "desktop.exit",
            new Dictionary<string, object?> { ["exitCode"] = e.ApplicationExitCode });
        base.OnExit(e);
    }

    static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UsageLog.LogEvent(
            "crash",
            "desktop.dispatcherUnhandled",
            new Dictionary<string, object?>
            {
                ["type"] = e.Exception.GetType().FullName,
                ["message"] = e.Exception.Message,
                ["stack"] = e.Exception.StackTrace,
            },
            error: e.Exception.Message);

        // A bug in one click handler must not throw away an operator's nest. Keep the process
        // alive for anything that is not a runtime-level failure and tell them what happened.
        if (e.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException)
            return;
        e.Handled = true;
        try
        {
            System.Windows.MessageBox.Show(
                "OmniCam 遇到内部错误，这一步没有完成；已写入使用日志。\n\n" +
                e.Exception.Message + "\n\n" +
                "建议：先「保存工程」，再重启 OmniCam。若反复出现，把日志目录连同工程文件交给维护人员。",
                "内部错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            /* the UI itself may be the problem; the log entry is what matters */
        }
    }

    static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        UsageLog.LogEvent(
            "crash",
            "desktop.domainUnhandled",
            new Dictionary<string, object?>
            {
                ["isTerminating"] = e.IsTerminating,
                ["type"] = ex?.GetType().FullName,
                ["message"] = ex?.Message,
                ["stack"] = ex?.StackTrace,
            },
            error: ex?.Message);
    }

    static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UsageLog.LogEvent(
            "crash",
            "desktop.unobservedTask",
            new Dictionary<string, object?>
            {
                ["message"] = e.Exception.Message,
                ["inner"] = e.Exception.InnerExceptions.Select(x => x.Message).Take(10).ToList(),
            },
            error: e.Exception.Message);
        e.SetObserved();
    }
}
