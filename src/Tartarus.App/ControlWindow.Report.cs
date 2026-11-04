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
    /// <summary>
    /// The frames scanned before the current one, oldest first, as JPEGs at half size: a bug often shows a second or
    /// two before it's reported. Six whole frames held 88 MB at 2560x1440 and 390 MB at 7680x2112 (2026-11-01).
    /// </summary>
    private readonly Queue<(DateTimeOffset At, byte[] Jpeg)> _history = new();
    private const int HistoryFrames = 4;
    // A report's pictures. Two PNGs made a report 4 MB, 11 at 4K, and the reports used up Sentry's attachment quota
    // (2026-11-02). A whole frame at 85 reads as its PNG does (48 of 48 report frames) at a seventh of the size; half a
    // frame doesn't (8 of 48 differ), so only those too big to need the detail are halved. The rest is only looked at.
    private const int ScreenQuality = 85, GlanceQuality = 70, WholeScreenWidth = 2560;
    private bool _reporting;

    /// <summary><paramref name="frame"/> takes the current one's place: that one goes to the history, its buffer back to the pool.</summary>
    private void RememberFrame(Frame frame)
    {
        if (_frame is not { } old || old == frame) return;
        _ = Task.Run(() =>
        {
            // Out of memory with the PC's memory nearly full (2026-11-03): the report does without this frame.
            try
            {
                BitmapSource small;
                try { small = HalfSize(old, _halves); }
                finally { _pixels.Return(old); }
                return ((DateTimeOffset At, byte[] Jpeg)?)(old.Timestamp, Encode(new JpegBitmapEncoder { QualityLevel = GlanceQuality }, small));
            }
            catch (OutOfMemoryException) { return null; }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (!t.IsCompletedSuccessfully || t.Result is not { } kept) return;
            _history.Enqueue(kept);
            while (_history.Count > HistoryFrames) _history.Dequeue();
        }));
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e) => Report();

    /// <summary>Gathers a report now and asks for its comment; <paramref name="crash"/> when an exception brought it up.</summary>
    public async void Report(Exception? crash = null)
    {
        if (_reporting) return;
        _reporting = true;
        try
        {
            BugReport report;
            try
            {
                report = Gather(crash);
                // The logs off the UI thread: a game's log may be hundreds of MB (2026-11-03: a hang in it).
                string? gameLog = _gameLogPath;
                await Task.Run(() => AddLogs(report, gameLog));
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
            report.Files["screen.jpg"] = Encode(new JpegBitmapEncoder { QualityLevel = ScreenQuality }, frame.Width > WholeScreenWidth ? Halved(screen) : screen);
            var shot = _overlay.View.Model is { } model && model.FrameWidth == frame.Width && model.FrameHeight == frame.Height ? WithOverlay(screen, model) : screen;
            report.Screenshot = Encode(new JpegBitmapEncoder { QualityLevel = GlanceQuality }, Halved(shot));
            foreach (var (at, jpeg) in _history)
                report.Files[$"history/{at.LocalDateTime:HHmmss_fff}.jpg"] = jpeg;
        }
        if (_scan is { } scan)
            report.AddText("scan.json", scan.RawJson.Length > 0 ? scan.RawJson : JsonSerializer.Serialize(scan, VisionJson.Default.ScanResult));
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        report.AddText("report.json", JsonSerializer.Serialize(State(crash), json));
        report.AddText("settings.json", JsonSerializer.Serialize(_settings, json));
        if (crash != null) report.AddText("crash.txt", crash.ToString());
        return report;
    }

    private static void AddLogs(BugReport report, string? gameLog)
    {
        AppLog.Flush();
        report.AddText("app.log", BugReports.Tail(AppSettings.LogPath, 3000));
        if (gameLog == null) return;
        try { report.AddText("game_s16.log", BugReports.GameLogRun(gameLog)); }
        catch (IOException ex) { report.AddText("game_s16.log", "unreadable: " + ex.Message); }
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
                captureWay = _capture?.GetType().Name,
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
                picksNet = _hints.Picks != null,
            },
        };
    }

    // The half-size pictures' buffers: the bitmap made of one copies it, so it goes back at once.
    private readonly PixelPool _halves = new();

    /// <summary>The frame at half its size (each 2x2 block's average), BGR: a quarter of the pixels to keep and encode.</summary>
    private static BitmapSource HalfSize(Frame frame, PixelPool pool)
    {
        int w = frame.Width / 2, h = frame.Height / 2, stride = frame.Stride;
        var half = pool.Rent(w * h * 4);
        var src = frame.Pixels;
        // Blue and red trade places in an RGBA frame.
        int b = frame.Format == Vision.PixelFormat.Rgba32 ? 2 : 0, r = 2 - b;
        for (int y = 0; y < h; y++)
        {
            int top = 2 * y * stride, bottom = top + stride, o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
            {
                int i = top + 8 * x, j = bottom + 8 * x;
                half[o] = (byte)((src[i + b] + src[i + 4 + b] + src[j + b] + src[j + 4 + b] + 2) >> 2);
                half[o + 1] = (byte)((src[i + 1] + src[i + 5] + src[j + 1] + src[j + 5] + 2) >> 2);
                half[o + 2] = (byte)((src[i + r] + src[i + 4 + r] + src[j + r] + src[j + 4 + r] + 2) >> 2);
            }
        }
        try
        {
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, half, w * 4);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pool.Return(half);
        }
    }

    private static BitmapSource Halved(BitmapSource bitmap)
    {
        var half = new TransformedBitmap(bitmap, new ScaleTransform(0.5, 0.5));
        half.Freeze();
        return half;
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
