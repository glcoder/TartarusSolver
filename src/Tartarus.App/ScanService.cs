using System.Collections.Concurrent;
using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

/// <summary>
/// Runs the Sandbox scanner on one dedicated below-normal-priority thread, so the game keeps the CPU first
/// and the UI never waits on it. The scanner is created on that thread at the first request.
/// </summary>
public sealed class ScanService : IDisposable
{
    private readonly BlockingCollection<(Frame? Frame, double? UiScale, TaskCompletionSource<ScanResult> Done)> _queue = new();
    private readonly Thread _thread;
    private readonly Func<ISandboxScanner> _factory;
    private ISandboxScanner? _scanner;
    private volatile bool _busy;
    private volatile string? _gameLanguage;

    public ScanService(int ocrThreads = 4)
    {
        _factory = () => CreateScanner(ocrThreads);
        _thread = new Thread(Run) { IsBackground = true, Name = "sandbox scanner", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool Busy => _busy;
    public string? ScannerInfo { get; private set; }
    /// <summary>Why the scanner couldn't start (a model missing, ...); scans then fail with it.</summary>
    public string? Problem { get; private set; }

    private ISandboxScanner CreateScanner(int threads)
    {
        try
        {
            var reader = new CnnCellReader(panels: new SlotReader());
            var presence = SandboxPresence.FindTemplate() is { } template ? SandboxPresence.Load(template) : null;
            return new LayoutScanner(new OcrEngine(threads: threads, gameLanguage: _gameLanguage), reader, presence);
        }
        catch (Exception ex)
        {
            Problem = Reason(ex);
            return new UnavailableScanner(Problem);
        }
    }

    /// <summary>
    /// The exception and what's inside it: a library that doesn't load says only "the type initializer threw" on top
    /// (2026-11-02: two players with no scanner at all, and no telling why). A DLL not found is most often the Visual C++
    /// runtime, which only the -full release brings along.
    /// </summary>
    private static string Reason(Exception ex)
    {
        var parts = new List<string>();
        bool dll = false;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            parts.Add(e == ex ? e.Message : $"{e.GetType().Name}: {e.Message}");
            dll |= e is DllNotFoundException;
        }
        string reason = string.Join(" <- ", parts);
        return dll ? reason + " Install the Microsoft Visual C++ Redistributable (x64): https://aka.ms/vs/17/release/vc_redist.x64.exe" : reason;
    }

    /// <summary>Stands in when the scanner can't start: every scan fails with the reason.</summary>
    private sealed class UnavailableScanner(string reason) : ISandboxScanner
    {
        public string Name => "no scanner";
        public ScanResult Scan(Frame frame) => ScanJson.Failure("Scanner failed to start: " + reason, Name);
        public void Dispose() { }
    }

    /// <summary>The game's language (its code): OCR reads with that language's recognizer from the next scan.</summary>
    public void SetGameLanguage(string? code)
    {
        _gameLanguage = code;
        if (_scanner is LayoutScanner layout) layout.Ocr.RequestLanguage(code);
    }

    /// <summary>The recognizer OCR reads with (its pack id), once the scanner is up.</summary>
    public string? OcrPack => (_scanner as LayoutScanner)?.Ocr.Pack.Id;

    /// <summary>Loads the models now instead of at the first scan.</summary>
    public Task WarmUpAsync() => Enqueue(null);

    /// <param name="uiScale">The game's UI scale as its log says it (<see cref="GameLog.UiScale"/>).</param>
    public Task<ScanResult> ScanAsync(Frame frame, double? uiScale = null) => Enqueue(frame, uiScale);

    private Task<ScanResult> Enqueue(Frame? frame, double? uiScale = null)
    {
        var tcs = new TaskCompletionSource<ScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _busy = true;
        _queue.Add((frame, uiScale, tcs));
        return tcs.Task;
    }

    private void Run()
    {
        foreach (var (frame, uiScale, done) in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (_scanner == null)
                {
                    _scanner = _factory();
                    // A language set while the models were loading.
                    if (_scanner is LayoutScanner layout) layout.Ocr.RequestLanguage(_gameLanguage);
                    ScannerInfo = _scanner.Name + (_scanner is LayoutScanner { Presence: null } ? " (no Sandbox template: tartarus-scan sandbox-template)" : "") + (Problem != null ? $" (scanner failed to start: {Problem})" : "");
                }
                if (_scanner is LayoutScanner scanner) scanner.LoggedUiScale = uiScale;
                done.SetResult(frame == null ? ScanJson.Failure("warm-up", _scanner.Name) : _scanner.Scan(frame));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                _busy = _queue.Count > 0;
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        // Still scanning after that (a slow PC): its models are left to the process's end, not freed under the scan.
        if (_thread.Join(2000)) _scanner?.Dispose();
    }
}
