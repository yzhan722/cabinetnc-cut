using System.Windows;
using CabinetNC.Desktop.Core;

namespace CabinetNC.Desktop;

static class UiDialog
{
    public static MessageBoxResult Show(
        Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        MessageBox.Show(owner, UiText.T(text), UiText.T(caption), buttons, image);

    public static MessageBoxResult Show(
        Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) =>
        MessageBox.Show(owner, UiText.T(text), UiText.T(caption), buttons, image, defaultResult);

    public static MessageBoxResult Show(
        string text, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        MessageBox.Show(UiText.T(text), UiText.T(caption), buttons, image);
}
