using System.Diagnostics;
using System.Windows;

namespace Tartarus.App;

/// <summary>New versions: asked of GitHub at start and every few hours, installed by the Update button.</summary>
public partial class ControlWindow
{
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(6);
    private Updates.Release? _update;
    private CancellationTokenSource? _updateChecks;
    private bool _updating;

    /// <summary>The checkbox as the settings have it (on the first time); the debug window neither shows it nor checks.</summary>
    private void SetupUpdates()
    {
        if (_settings.Debug)
        {
            UpdatePanel.Visibility = Visibility.Collapsed;
            return;
        }
        Updates.CleanUp();
        UpdateCheck.IsChecked = _settings.CheckUpdates;
    }

    private void UpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool on = UpdateCheck.IsChecked == true;
        if (_settings.CheckUpdates != on)
        {
            _settings.CheckUpdates = on;
            _settings.Save();
        }
        _updateChecks?.Cancel();
        _updateChecks = null;
        if (on)
        {
            _updateChecks = new CancellationTokenSource();
            _ = CheckUpdatesAsync(_updateChecks.Token);
        }
        else if (!_updating) UpdateButton.Visibility = Visibility.Collapsed;
    }

    private async Task CheckUpdatesAsync(CancellationToken ct)
    {
        bool failed = false, latestSaid = false;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            while (true)
            {
                try
                {
                    var latest = await Updates.LatestAsync(ct);
                    if (latest != null && latest.Version > Updates.Current && latest.Version != _update?.Version)
                    {
                        _update = latest;
                        Log($"Update: version {latest.Version} is out ({latest.Page})");
                        ShowUpdate(latest);
                    }
                    else if (latest != null && latest.Version <= Updates.Current && !latestSaid)
                    {
                        // Once a start: the check ran and found nothing newer.
                        latestSaid = true;
                        Log($"Update check: {AppVersion.Number} is the latest version (GitHub's latest is {latest.Version})");
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Offline, or GitHub not answering: said once, asked again later.
                    if (!failed) Log("Update check failed: " + ex.Message);
                    failed = true;
                }
                await Task.Delay(UpdateEvery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void ShowUpdate(Updates.Release release)
    {
        bool install = release.Url != null && Updates.Installed;
        UpdateButton.Content = $"Update to {release.Version}";
        string notes = release.Notes.Length > 1500 ? release.Notes[..1500] + "…" : release.Notes;
        UpdateButton.ToolTip = (install ? $"Download {release.Asset} ({release.Size / 1048576.0:0} MB), install it and restart" : "Open the release's page")
            + (notes.Length > 0 ? "\n\n" + notes : "");
        UpdateButton.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } release || _updating) return;
        // A build from source, or a release without this install's zip: the page, to take it by hand.
        if (release.Url == null || !Updates.Installed)
        {
            Process.Start(new ProcessStartInfo(release.Page) { UseShellExecute = true });
            return;
        }
        if (MessageBox.Show(this, $"Download version {release.Version} ({release.Size / 1048576.0:0} MB), put it in place of this one and restart the app?",
                "Tartarus Solver: update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _updating = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateButton.Content = $"Downloading… {p * 100:0}%");
            string app = await Updates.DownloadAsync(release, progress);
            Log($"Update: {release.Asset} downloaded and checked, installing");
            Updates.StartInstall(app, Environment.GetCommandLineArgs().Skip(1));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log("Update failed: " + ex.Message);
            MessageBox.Show(this, $"The update failed: {ex.Message}", "Tartarus Solver: update", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateButton.Content = $"Update to {release.Version}";
            UpdateButton.IsEnabled = true;
            _updating = false;
        }
    }
}
