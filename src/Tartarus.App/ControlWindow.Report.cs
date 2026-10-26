using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tartarus.Vision;
using Frame = Tartarus.Vision.Frame;

namespace Tartarus.App;

// Bug reports: what the app saw and thought at the moment the player asked, gathered at once (the comment is typed
// after), then sent with the comment.
public partial class ControlWindow
{
    /// <summary>The frames scanned before the current one, oldest first: a bug often shows a second or two before it's reported.</summary>
    private readonly Queue<Frame> _history = new();
    private const int HistoryFrames = 6;
    private bool _reporting;

    private void RememberFrame(Frame frame)
    {
        if (_frame != null && _frame != frame) _history.Enqueue(_frame);
        while (_history.Count > HistoryFrames) _history.Dequeue();
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e) => Report();

    /// <summary>Gathers a report now and asks for its comment; <paramref name="crash"/> when an exception brought it up.</summary>
    public void Report(Exception? crash = null)
    {
        if (_reporting) return;
        _reporting = true;
        try
        {
            BugReport report;
            try
            {
                report = Gather(crash);
            }
            catch (Exception ex)
            {
                // A report of what's left rather than none.
                report = new BugReport { Crash = crash };
                report.AddText("gather-error.txt", ex.ToString());
            }
            PlaceOverlay();
            var window = new ReportWindow(report);
            if (IsVisible) window.Owner = this;
            if (window.ShowDialog() == true && window.Result != null) Log(window.Result);
        }
        finally
        {
            _reporting = false;
            PlaceOverlay();
        }
    }

    private BugReport Gather(Exception? crash)
    {
        var report = new BugReport { Crash = crash };
        if (_frame is { } frame)
        {
            var screen = ToBitmap(frame);
            report.Files["screen.png"] = Encode(new PngBitmapEncoder(), screen);
            if (_overlay.View.Model is { } model && model.FrameWidth == frame.Width && model.FrameHeight == frame.Height)
            {
                var shot = WithOverlay(screen, model);
                report.Files["overlay.png"] = Encode(new PngBitmapEncoder(), shot);
                report.Screenshot = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, shot);
            }
            else report.Screenshot = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, screen);
            foreach (var old in _history)
                report.Files[$"history/{old.Timestamp.LocalDateTime:HHmmss_fff}.jpg"] = Encode(new JpegBitmapEncoder { QualityLevel = 80 }, ToBitmap(old));
        }
        if (_scan is { } scan)
            report.AddText("scan.json", scan.RawJson.Length > 0 ? scan.RawJson : JsonSerializer.Serialize(scan, VisionJson.Default.ScanResult));
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        report.AddText("report.json", JsonSerializer.Serialize(State(crash), json));
        report.AddText("settings.json", JsonSerializer.Serialize(_settings, json));
        report.AddText("app.log", BugReports.Tail(AppSettings.LogPath, 3000));
        if (_gameLogPath != null)
        {
            try { report.AddText("game_s16.log", BugReports.GameLogRun(_gameLogPath)); }
            catch (IOException ex) { report.AddText("game_s16.log", "unreadable: " + ex.Message); }
        }
        if (crash != null) report.AddText("crash.txt", crash.ToString());
        return report;
    }

    /// <summary>What the app knew: version, window, what it read, the run's memory and the log's, the hint and its ranking.</summary>
    private object State(Exception? crash)
    {
        var hint = _hint;
        return new
        {
            version = AppVersion.Number,
            built = AppVersion.Built,
            models = AppVersion.Models.Select(m => new { m.Name, m.Hash, m.Trained }),
            scanner = _scans.ScannerInfo,
            os = Environment.OSVersion.VersionString,
            at = DateTimeOffset.Now,
            crash = crash?.Message,
            window = _target is { } t ? new { t.Title, frame = _frame is { } f ? $"{f.Width}x{f.Height}" : null, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip } : null,
            frameAt = _frame?.Timestamp,
            sandbox = _sandbox is { } s ? SandboxText.Describe(s) : null,
            read = _scannedSandbox is { } r ? SandboxText.Describe(r) : null,
            scan = _scan is { } scan ? SandboxText.Describe(scan) : null,
            run = new { theme = _run.Theme, held = _run.Held, kept = _run.Kept, chromatic = _run.Chromatic, rerolls = _rerolls },
            gameLog = _gameLogPath,
            logRun = _logRun,
            hint = hint == null ? null : new
            {
                state = Tartarus.Rules.StateText.Describe(hint.State),
                hint.ConsumeOnly,
                hint.Final,
                hint.Result.Playouts,
                ms = hint.Result.Elapsed.TotalMilliseconds,
                actions = hint.Result.Actions.Select(a => new
                {
                    name = SandboxOverlayView.ActionName(a.Action),
                    action = a.Action.ToString(),
                    a.Chromatic,
                    a.Mean,
                    a.StdError,
                    a.Playouts,
                }),
            },
            eyeHint = _eyeHint,
            // What settings.json doesn't hold: the window's state as it is now.
            session = new
            {
                capturing = _capture != null,
                autoAttach = AutoAttachCheck.IsChecked == true,
                autoScan = AutoScanCheck.IsChecked == true,
                overlay = OverlayCheck.IsChecked == true,
                overlayShown = _overlay.IsVisible,
                regions = BoxesCheck.IsChecked == true,
                ocrText = TextCheck.IsChecked == true,
                editMode = _editMode,
                stale = _stale,
                hints = _settings.Hints,
                hintsOnly = _settings.HintsOnly,
                hintPlayouts = _hints.Playouts,
                valueNet = _hints.Net != null,
                policyNet = _hints.Policy != null,
            },
        };
    }

    private static BitmapSource ToBitmap(Frame frame)
    {
        byte[] pixels = frame.Pixels;
        if (frame.Format == Vision.PixelFormat.Rgba32)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 0; i + 3 < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, pixels, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The frame with the overlay drawn on it as the player saw it, at the frame's size.</summary>
    private static BitmapSource WithOverlay(BitmapSource screen, OverlayModel model)
    {
        int w = screen.PixelWidth, h = screen.PixelHeight;
        var view = new SandboxOverlayView { Model = model, Width = w, Height = h };
        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawImage(screen, new Rect(0, 0, w, h));
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(background);
        target.Render(view);
        target.Freeze();
        return target;
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
