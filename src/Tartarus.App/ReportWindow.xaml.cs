using System.Windows;
using Tartarus.App.Localization;

namespace Tartarus.App;

/// <summary>The bug report's comment: what was gathered when it was asked for goes with it on Send.</summary>
public partial class ReportWindow : Window
{
    private readonly BugReport _report;

    /// <summary>What sending said, for the app's log; null when cancelled.</summary>
    public string? Result { get; private set; }

    public ReportWindow(BugReport report)
    {
        _report = report;
        InitializeComponent();
        OnlyTartarus.Text = Strings.Report_OnlyTartarus;
        if (report.Crash != null)
        {
            // The app's own error: wherever it happened, it's worth telling.
            OnlyTartarus.Visibility = Visibility.Collapsed;
            Prompt.Text = Strings.Report_AppError;
        }
        Loc.Apply(this);
        Loaded += (_, _) =>
        {
            Activate();
            CommentBox.Focus();
        };
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = CancelButton.IsEnabled = CommentBox.IsEnabled = false;
        StatusText.Text = Strings.Report_Sending;
        StatusText.Visibility = Visibility.Visible;
        string comment = CommentBox.Text;
        Result = await Task.Run(() => BugReports.SendAsync(_report, comment));
        // Closed by its title bar's button while sending: no dialog left to answer (a crash on 0.7.21).
        if (IsVisible) DialogResult = true;
    }

    // Shift+Enter sends; Enter alone is a new line.
    private void CommentBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) || !SendButton.IsEnabled) return;
        e.Handled = true;
        SendButton_Click(sender, e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
