using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Tartarus.Capture;

public readonly record struct WindowInfo(IntPtr Hwnd, string Title, string Process, int Pid)
{
    public override string ToString() => $"{Title}  [{Process}]";
}

/// <summary>Rectangle in physical (per-monitor DPI aware) screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Top-level window queries. Read-only: nothing here sends input to or changes another window.</summary>
public static partial class Win32Windows
{
    /// <summary>Title of the game's window (same as the reference desktop app looks for).</summary>
    public static readonly Regex GameTitle = new(@"^Torchlight: Infinite\s*$", RegexOptions.Compiled);

    public static List<WindowInfo> List(bool includeOwnProcess = false)
    {
        var list = new List<WindowInfo>();
        int self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsCloaked(h)) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            string title = GetTitle(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out int pid);
            if (!includeOwnProcess && pid == self) return true;
            list.Add(new WindowInfo(h, title, ProcessName(pid), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The launcher's window has the same title as the game's; it is never the game.</summary>
    public static bool IsLauncher(WindowInfo w) => w.Process.Contains("Launcher", StringComparison.OrdinalIgnoreCase);

    /// <summary>The window whose title matches <paramref name="title"/> (the game's by default), leaving out the launcher.</summary>
    public static WindowInfo? FindGame(Regex? title = null) =>
        List().FirstOrDefault(w => (title ?? GameTitle).IsMatch(w.Title) && !IsLauncher(w)) is { Hwnd: not 0 } w ? w : null;

    public static string GetTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool Exists(IntPtr h) => h != IntPtr.Zero && IsWindow(h);
    public static bool IsMinimized(IntPtr h) => IsIconic(h);
    public static IntPtr Foreground => GetForegroundWindow();

    public static int ProcessIdOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out int pid);
        return pid;
    }

    /// <summary>
    /// The full path of a process's executable ("G:\...\torchlight_infinite.exe"), from the system's process list
    /// (NtQuerySystemInformation, SystemProcessIdInformation): no handle to the process is opened, nothing of it is
    /// read. Null when Windows doesn't say.
    /// </summary>
    public static unsafe string? ImagePath(int pid)
    {
        const int SystemProcessIdInformation = 0x58;
        char* buffer = stackalloc char[1024];
        var info = new SystemProcessIdInfo
        {
            ProcessId = pid,
            ImageName = new UnicodeString { Length = 0, MaximumLength = 1024 * sizeof(char), Buffer = (IntPtr)buffer },
        };
        if (NtQuerySystemInformation(SystemProcessIdInformation, &info, sizeof(SystemProcessIdInfo), out _) != 0) return null;
        string device = new(buffer, 0, info.ImageName.Length / sizeof(char));
        return DosPath(device);
    }

    /// <summary>"\Device\HarddiskVolume5\Games\x.exe" as "G:\Games\x.exe", by the drive letters' device names.</summary>
    private static string? DosPath(string devicePath)
    {
        var target = new StringBuilder(512);
        foreach (var drive in Environment.GetLogicalDrives())
        {
            string letter = drive.TrimEnd('\\');
            if (QueryDosDevice(letter, target, target.Capacity) == 0) continue;
            string device = target.ToString();
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return letter + devicePath[device.Length..];
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInfo
    {
        public nint ProcessId;
        public UnicodeString ImageName;
    }

    [DllImport("ntdll.dll")] private static extern unsafe int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int QueryDosDevice(string device, StringBuilder target, int max);

    /// <summary>
    /// The window's visible bounds (DWM extended frame bounds, without the invisible resize border),
    /// which is what Windows.Graphics.Capture puts in the frame.
    /// </summary>
    public static PixelRect VisibleBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
            return new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        GetWindowRect(h, out r);
        return new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The window's client area (what the game renders, without title bar and borders), in screen pixels.</summary>
    public static PixelRect ClientBounds(IntPtr h)
    {
        if (!GetClientRect(h, out RECT c)) return default;
        var origin = new POINT();
        if (!ClientToScreen(h, ref origin)) return default;
        return new PixelRect(origin.X, origin.Y, origin.X + c.Right, origin.Y + c.Bottom);
    }

    /// <summary>
    /// Makes this process per-monitor DPI aware (V2), so window rectangles come in physical pixels like the
    /// captured frames. The WPF app declares it in its manifest; console tools call this first.
    /// </summary>
    public static void EnableDpiAwareness() => SetProcessDpiAwarenessContext(-4);

    private static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "?"; }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr h, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
