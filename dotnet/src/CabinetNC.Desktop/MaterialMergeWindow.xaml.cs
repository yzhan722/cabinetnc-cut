using System.Windows;
using System.Windows.Controls;
using CabinetNC.Domain;
using CabinetNC.Domain.Nesting;
using CabinetNC.Domain.Parts;
using PanelPart = CabinetNC.Domain.Parts.Panel;

namespace CabinetNC.Desktop;

public sealed class MaterialKindOption
{
    public required NestGroupKey Key { get; init; }
    public required string Label { get; init; }
    public int PanelCount { get; init; }
    public string Display => $"{Label}（{PanelCount} 件）";
}

public partial class MaterialMergeWindow : Window
{
    readonly IReadOnlyList<PanelPart> _selectedPanels;

    public NestGroupKey? ChosenKey { get; private set; }
    public BlindFeatureDepthPolicy BlindPolicy { get; private set; } = BlindFeatureDepthPolicy.Keep;

    public MaterialMergeWindow(IReadOnlyList<MaterialKindOption> kinds, IReadOnlyList<PanelPart> selectedPanels)
    {
        InitializeComponent();
        _selectedPanels = selectedPanels;
        KindList.ItemsSource = kinds;
        BlindPane.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            var prefer = kinds.OrderByDescending(k => k.PanelCount).First();
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
        var losers = ChosenKey is { } target
            ? _selectedPanels.Where(p => !NestGroupKey.From(p.Material, p.ThicknessMm).Equals(target))
            : [];
        BlindPane.Visibility = MaterialCorrect.HasHalfSlotOrHinge(losers)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (ChosenKey is null)
        {
            UiDialog.Show(this, "请选择合并后的材料。", "合并材料", MessageBoxButton.OK, MessageBoxImage.Information);
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
