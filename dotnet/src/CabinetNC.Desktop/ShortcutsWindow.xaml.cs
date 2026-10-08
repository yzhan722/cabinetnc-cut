using System.Windows;
using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        Groups.ItemsSource = ShortcutCatalog.All.GroupBy(s => s.Group).ToList();
    }
}
