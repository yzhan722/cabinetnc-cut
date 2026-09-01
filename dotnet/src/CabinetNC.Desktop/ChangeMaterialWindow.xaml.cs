using System.Windows;
using System.Windows.Controls;
using CabinetNC.Domain;
using CabinetNC.Domain.Nesting;
using PanelPart = CabinetNC.Domain.Parts.Panel;

namespace CabinetNC.Desktop;

public partial class ChangeMaterialWindow : Window
{
    readonly IReadOnlyList<PanelPart> _selectedPanels;

    public NestGroupKey? ChosenKey { get; private set; }
    public BlindFeatureDepthPolicy BlindPolicy { get; private set; } = BlindFeatureDepthPolicy.Keep;

    public ChangeMaterialWindow(
        IReadOnlyList<MaterialKindOption> kinds,
        IReadOnlyList<PanelPart> selectedPanels,
        NestGroupKey? preferKey = null)
    {
        InitializeComponent();
        _selectedPanels = selectedPanels;
        KindList.ItemsSource = kinds;
        LeadText.Text = selectedPanels.Count > 1
            ? $"将 {selectedPanels.Count} 件改为"
            : "将板件改为";
        BlindPane.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            var prefer = preferKey is { } key
                ? kinds.FirstOrDefault(k => k.Key.Equals(key))
                : null;
            prefer ??= kinds.FirstOrDefault();
            if (prefer is null) return;
            foreach (var radio in FindRadios(KindList))
            {
                if (radio.Tag is MaterialKindOption opt && opt.Key.Equals(prefer.Key))
                {
                    radio.IsChecked = true;
                    break;
                }
            }
        };
    }

    void OnKindChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: MaterialKindOption opt })
            ChosenKey = opt.Key;
        RefreshBlindPane();
    }

    void RefreshBlindPane()
    {
        var changing = ChosenKey is { } target
            ? _selectedPanels.Where(p => !NestGroupKey.From(p.Material, p.ThicknessMm).Equals(target))
            : [];
        BlindPane.Visibility = MaterialCorrect.HasHalfSlotOrHinge(changing)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (ChosenKey is null)
        {
            MessageBox.Show(this, "请选择材料。", "改变材料", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        BlindPolicy = BlindScale.IsChecked == true
            ? BlindFeatureDepthPolicy.ScaleWithThickness
            : BlindFeatureDepthPolicy.Keep;
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    static IEnumerable<RadioButton> FindRadios(DependencyObject root)
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton radio)
                yield return radio;
            foreach (var nested in FindRadios(child))
                yield return nested;
        }
    }
}
