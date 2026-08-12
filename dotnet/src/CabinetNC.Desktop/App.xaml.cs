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
