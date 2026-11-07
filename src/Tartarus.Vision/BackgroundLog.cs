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
    private readonly long _maxBytes;
    private readonly Queue<string> _waiting = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private long _added, _done;
    private volatile bool _stop;

    /// <param name="maxBytes">A file grown past this is cut to its last quarter as the writer starts (0: never).</param>
    public BackgroundLog(string path, long maxBytes = 0)
    {
        _path = path;
        _maxBytes = maxBytes;
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
        Trim();
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

    /// <summary>Only the file's last quarter kept, from a line's start, once it's past the limit (2026-11-07: players' logs grew 0.1-0.2 MB a run, never cut).</summary>
    private void Trim()
    {
        try
        {
            var info = new FileInfo(_path);
            if (_maxBytes <= 0 || !info.Exists || info.Length <= _maxBytes) return;
            var tail = new byte[_maxBytes / 4];
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(-tail.Length, SeekOrigin.End);
                stream.ReadExactly(tail);
            }
            int start = Array.IndexOf(tail, (byte)'\n') + 1;
            string tmp = _path + ".tmp";
            using (var file = File.Create(tmp)) file.Write(tail, start, tail.Length - start);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
