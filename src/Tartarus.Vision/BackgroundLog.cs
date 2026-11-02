using System.Text;

namespace Tartarus.Vision;

/// <summary>
/// Lines appended to a file on a thread of their own: whoever logs never waits for the disk (2026-11-02, a player's hang
/// reports: the window stood still for over 5 s in a log line's WriteFile). A write that fails is tried again; a file that
/// can't be written for long loses its oldest lines rather than filling the memory.
/// </summary>
public sealed class BackgroundLog : IDisposable
{
    private const int MaxWaiting = 10_000, RetryMs = 500;

    private readonly string _path;
    private readonly Queue<string> _waiting = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private long _added, _done;
    private volatile bool _stop;

    public BackgroundLog(string path)
    {
        _path = path;
        _thread = new Thread(Run) { IsBackground = true, Name = "log writer" };
        _thread.Start();
    }

    /// <summary>Queues a line; returns at once.</summary>
    public void Write(string line)
    {
        lock (_lock)
        {
            _waiting.Enqueue(line);
            _added++;
            if (_waiting.Count > MaxWaiting)
            {
                _waiting.Dequeue();
                _done++;
            }
        }
        _wake.Set();
    }

    /// <summary>Waits for the lines queued so far to reach the file, at most <paramref name="timeoutMs"/>; false if they didn't.</summary>
    public bool Flush(int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        lock (_lock)
        {
            long target = _added;
            while (_done < target)
            {
                long left = until - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_lock, (int)left);
            }
        }
        return true;
    }

    private void Run()
    {
        var batch = new StringBuilder();
        while (!_stop)
        {
            _wake.WaitOne();
            while (!_stop)
            {
                int lines;
                lock (_lock)
                {
                    lines = _waiting.Count;
                    if (lines == 0) break;
                    batch.Clear();
                    foreach (var line in _waiting) batch.Append(line).Append(Environment.NewLine);
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                    File.AppendAllText(_path, batch.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(RetryMs);
                    continue;
                }
                lock (_lock)
                {
                    // Lines dropped meanwhile were counted done already: only those still waiting go.
                    for (int i = 0; i < lines && _waiting.Count > 0; i++)
                    {
                        _waiting.Dequeue();
                        _done++;
                    }
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
