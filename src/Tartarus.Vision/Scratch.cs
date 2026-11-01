namespace Tartarus.Vision;

/// <summary>
/// A buffer reused call after call by one reader at a time. The networks' inputs (and the text detector's region
/// labels) were new arrays at every scan, most of them big enough for the large object heap: 5.7 MB a second off the
/// board, and the garbage collector let the process grow to 1.6 GB before giving it back (2026-11-01).
/// </summary>
internal sealed class Scratch<T> where T : struct
{
    private T[] _buffer = [];

    /// <summary>Its first <paramref name="length"/> items, cleared.</summary>
    public Memory<T> Take(int length)
    {
        if (_buffer.Length < length) _buffer = new T[length];
        var memory = _buffer.AsMemory(0, length);
        memory.Span.Clear();
        return memory;
    }
}
