using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Tartarus.Vision;

namespace Tartarus.Capture;

/// <summary>
/// The fallback for a Windows that won't capture a window (older than 1903, its capture service off or removed): a GDI copy
/// of the part of the screen where the game's client area is. Nothing is asked of the game's window. What's on top of the game
/// comes along, so no frame is made while another window is over it. The overlay would come along too, layered or not (seen
/// 2026-11-04): the app leaves it out of captures, or where Windows can't (before 10 2004) has it cloaked for each copy. The
/// cursor doesn't come along (no CAPTUREBLT).
/// </summary>
public sealed class ScreenCopyCapture : IGameCapture
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int DWMWA_CLOAK = 13;

    private readonly object _lock = new();
    private readonly HashSet<IntPtr> _ignored = [];
    private readonly List<IntPtr> _cloaked = [];
    private readonly HashSet<string> _told = [];
    private IntPtr _bitmap;
    private IntPtr _bits;
    private int _width, _height;
    private bool _disposed, _closed;

    public ScreenCopyCapture(IntPtr hwnd)
    {
        Hwnd = hwnd;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "the screen can't be read");
        ReleaseDC(IntPtr.Zero, screen);
    }

    public IntPtr Hwnd { get; }
    public bool CropToClient { get; set; } = true;
    public PixelRect LastBounds { get; private set; }
    public event Action? Closed;
    public event Action<string>? Note;

    /// <summary>Another window stood over the game at the last grab: no frame came of it.</summary>
    public bool Covered { get; private set; }

    /// <summary>A window of ours that stays out of the copies (Windows 10 2004 and later can do that): it covers nothing.</summary>
    public void Ignore(IntPtr window)
    {
        lock (_lock) _ignored.Add(window);
    }

    /// <summary>A window of ours that's taken off the screen for each copy: it blinks, but isn't in the frames.</summary>
    public void CloakForCopies(IntPtr window)
    {
        lock (_lock) _cloaked.Add(window);
    }

    public unsafe Frame? Grab(PixelPool? pool = null)
    {
        lock (_lock)
        {
            if (_disposed) return null;
            if (!Win32Windows.Exists(Hwnd))
            {
                if (!_closed)
                {
                    _closed = true;
                    Closed?.Invoke();
                }
                return null;
            }
            if (Win32Windows.IsMinimized(Hwnd)) return null;
            var client = Win32Windows.ClientBounds(Hwnd);
            if (client.IsEmpty) return null;
            IntPtr over = Win32Windows.WindowOver(Hwnd, client, _ignored);
            Covered = over != IntPtr.Zero;
            if (Covered)
            {
                Tell($"Screen copy: no frames while a window is over the game: class \"{Win32Windows.ClassName(over)}\" of {Win32Windows.ProcessName(Win32Windows.ProcessIdOf(over))} at {Win32Windows.VisibleBounds(over)}, game's client area {client}");
                return null;
            }
            LastBounds = client;
            int w = client.Width, h = client.Height;

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "the screen can't be read");
            try
            {
                IntPtr memory = CreateCompatibleDC(screen);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "no memory device context");
                try
                {
                    if (_bitmap == IntPtr.Zero || _width != w || _height != h) NewBitmap(memory, w, h);
                    IntPtr old = SelectObject(memory, _bitmap);
                    bool copied;
                    int error;
                    Cloak(true);
                    try
                    {
                        copied = BitBlt(memory, 0, 0, w, h, screen, client.Left, client.Top, SRCCOPY);
                        error = Marshal.GetLastWin32Error();
                    }
                    finally
                    {
                        Cloak(false);
                    }
                    SelectObject(memory, old);
                    if (!copied) throw new Win32Exception(error, "the screen copy failed");
                    GdiFlush();
                }
                finally
                {
                    DeleteDC(memory);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            var pixels = pool?.Rent(w * h * 4) ?? new byte[w * h * 4];
            // GDI leaves the alpha as it was: frames are opaque.
            var source = new ReadOnlySpan<uint>((void*)_bits, w * h);
            var target = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, w * h * 4));
            var opaque = new Vector<uint>(0xFF000000);
            int i = 0;
            for (; i <= source.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                (new Vector<uint>(source[i..]) | opaque).CopyTo(target[i..]);
            for (; i < source.Length; i++) target[i] = source[i] | 0xFF000000;
            return new Frame(w, h, pixels, PixelFormat.Bgra32, DateTimeOffset.Now);
        }
    }

    private void Cloak(bool on)
    {
        if (_cloaked.Count == 0) return;
        int value = on ? 1 : 0;
        foreach (var window in _cloaked) DwmSetWindowAttribute(window, DWMWA_CLOAK, ref value, sizeof(int));
        // The next copy of the screen has it gone: the desktop is composed anew first.
        if (on) DwmFlush();
    }

    private void Tell(string note)
    {
        if (_told.Add(note)) Note?.Invoke(note);
    }

    private void NewBitmap(IntPtr dc, int w, int h)
    {
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _bitmap = CreateDIBSection(dc, ref info, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
        {
            _width = _height = 0;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "no bitmap for the screen copy");
        }
        (_width, _height) = (w, h);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
