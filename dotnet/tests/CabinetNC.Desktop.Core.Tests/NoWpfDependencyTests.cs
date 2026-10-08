using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

/// <summary>
/// The whole point of Desktop.Core is that it runs (and is tested) without WPF. If someone
/// drags a UI type across the boundary, this fails on the Linux CI before anything else.
/// </summary>
public class NoWpfDependencyTests
{
    [Fact]
    public void Core_assembly_references_no_wpf_assemblies()
    {
        var refs = typeof(StatusInference).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "SkiaSharp.Views.WPF"];
        var leaked = refs.Where(r => forbidden.Any(f => r.StartsWith(f, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.True(leaked.Count == 0, "Desktop.Core must stay WPF-free; found: " + string.Join(", ", leaked));
    }
}
