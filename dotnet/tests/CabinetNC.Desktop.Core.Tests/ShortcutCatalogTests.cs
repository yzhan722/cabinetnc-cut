using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop.Core.Tests;

public class ShortcutCatalogTests
{
    static string MainWindowSource()
    {
        var walk = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var p = Path.Combine(walk, "dotnet", "src", "CabinetNC.Desktop", "MainWindow.xaml.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            var parent = Directory.GetParent(walk);
            if (parent is null) break;
            walk = parent.FullName;
        }
        throw new FileNotFoundException("MainWindow.xaml.cs not found");
    }

    [Fact]
    public void Every_listed_key_binding_exists_in_the_window_source()
    {
        // Cheap drift guard: the handler must still mention the keys the sheet advertises.
        var src = MainWindowSource();
        string[] mustMention =
        [
            "Key.O", "Key.S", "Key.E", "Key.D1", "Key.D5", "Key.Z", "Key.Y", "Key.C", "Key.X", "Key.V",
            "Key.Delete", "Key.Enter", "Key.Escape", "Key.F", "Key.Home", "Key.OemPlus", "Key.OemMinus", "Key.Space", "Key.D",
        ];
        var missing = mustMention.Where(k => !src.Contains(k, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "shortcut sheet lists keys the window no longer handles: " + string.Join(", ", missing));
    }

    [Fact]
    public void Catalog_is_grouped_and_free_of_duplicates()
    {
        Assert.True(ShortcutCatalog.All.Count >= 15);
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.All.Select(s => s.Keys + "|" + s.Group).Distinct().Count());
        Assert.All(ShortcutCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Action)));
        Assert.Equal(["文件", "导航", "编辑", "视口", "密排", "仿真"], ShortcutCatalog.All.Select(s => s.Group).Distinct());
    }
}
