using System.Windows;
using System.Windows.Controls;

namespace CabinetNC.Desktop;

/// <summary>
/// Preflight soft failures used to be a Yes/No box. Forcing an export is a machine-safety
/// decision, so the operator now has to state why; the reason is returned to the caller
/// for the usage log.
/// </summary>
public partial class OverrideReasonWindow : Window
{
    const int MinReasonLength = 6;

    public string Reason { get; private set; } = "";

    public OverrideReasonWindow(string issues)
    {
        InitializeComponent();
        IssuesText.Text = issues;
        Loaded += (_, _) => ReasonBox.Focus();
    }

    void OnReasonChanged(object sender, TextChangedEventArgs e) =>
        ConfirmBtn.IsEnabled = ReasonBox.Text.Trim().Length >= MinReasonLength;

    void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Reason = ReasonBox.Text.Trim();
        if (Reason.Length < MinReasonLength) return;
        DialogResult = true;
        Close();
    }
}
