namespace Tartarus.Vision;

/// <summary>
/// Frame buffers given back once nobody reads them, for the next grab of the same size: a 2560x1440 frame is 14.7 MB
/// (65 MB at 7680x2112), and one allocated a scan made the garbage collector sweep the whole heap every few seconds
/// (2026-11-01: 156 MB allocated in 20 s off the board). Any thread.
/// </summary>
public sealed class PixelPool
{
    private const int Keep = 2;
    private readonly Stack<byte[]> _free = new();

    /// <summary>A buffer of exactly <paramref name="size"/> bytes: a given-back one, else a new one.</summary>
    public byte[] Rent(int size)
    {
        lock (_free)
            while (_free.TryPop(out var buffer))
                if (buffer.Length == size) return buffer;
        return new byte[size];
    }

    /// <summary>A frame's buffer back, unless the frame is kept (<see cref="Frame.Keep"/>): no one may read it after this.</summary>
    public void Return(Frame frame)
    {
        if (!frame.Kept) Return(frame.Pixels);
    }

    /// <summary>A buffer back from <see cref="Rent"/>: no one may read it after this.</summary>
    public void Return(byte[] buffer)
    {
        lock (_free)
            if (_free.Count < Keep) _free.Push(buffer);
    }
}
