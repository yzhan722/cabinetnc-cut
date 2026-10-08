using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace CabinetNC.Desktop;

/// <summary>
/// Version, commit and environment in one copyable block. The shop log requires the
/// commit SHA for every machine run; this is where the operator gets it.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow(string machineId, string libraryPath)
    {
        InitializeComponent();
        BuildInfoBox.Text = BuildInfo(machineId, libraryPath);
    }

    static string BuildInfo(string machineId, string libraryPath)
    {
        var asm = typeof(AboutWindow).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? asm.GetName().Version?.ToString()
                            ?? "?";
        // .NET SDK appends "+<git sha>" to the informational version when built inside a repo.
        var plus = informational.IndexOf('+');
        var version = plus > 0 ? informational[..plus] : informational;
        var sha = plus > 0 ? informational[(plus + 1)..] : "(未嵌入)";
        var built = System.IO.File.GetLastWriteTime(asm.Location);
        return
            $"OmniCam {version}\n" +
            $"commit  {sha}\n" +
            $"built   {built:yyyy-MM-dd HH:mm}\n" +
            $"runtime {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription}\n" +
            $"machine {machineId}\n" +
            $"library {libraryPath}";
    }

    void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildInfoBox.Text);
        }
        catch
        {
            // clipboard can be locked by another process; the text stays visible to copy manually
        }
    }
}
