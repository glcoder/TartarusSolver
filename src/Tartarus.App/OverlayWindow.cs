using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Tartarus.Capture;

namespace Tartarus.App;

/// <summary>
/// Borderless, transparent, topmost window laid exactly over the captured window. Click-through and
/// never activated (WS_EX_LAYERED | TRANSPARENT | TOPMOST | TOOLWINDOW | NOACTIVATE); in edit mode it
/// drops WS_EX_TRANSPARENT so cells can be clicked, still without taking focus from the game.
/// </summary>
public sealed class OverlayWindow : Window
{
    private IntPtr _hwnd;
    private PixelRect _placed;

    public OverlayWindow()
    {
        Title = "Tartarus overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Width = 10;
        Height = 10;
        View = new SandboxOverlayView();
        Content = View;
    }

    public SandboxOverlayView View { get; }
    public IntPtr Handle => _hwnd;
    public bool ClickThrough { get; private set; } = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes the window by the DPI ratio when it crosses monitors; put it back at the next placement.
        _placed = default;
    }

    public void SetClickThrough(bool on)
    {
        ClickThrough = on;
        if (_hwnd == IntPtr.Zero) return;
        if (on) Native.AddExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
        else Native.RemoveExStyle(_hwnd, Native.WS_EX_TRANSPARENT);
    }

    /// <summary>Moves the overlay onto <paramref name="r"/> (physical pixels) and keeps it on top of <paramref name="game"/>.</summary>
    public void PlaceOver(PixelRect r, IntPtr game)
    {
        if (_hwnd == IntPtr.Zero || r.IsEmpty) return;
        // In place and still on top: nothing to tell Windows. Each SetWindowPos has WPF wait on its render thread
        // (HwndTarget.UpdateWindowSettings, SyncFlush), where the UI stood in players' hang reports; it was sent five times
        // a second (2026-11-01). A borderless game window can climb above us when it's activated: then again.
        if (r == _placed && OnTopOf(game)) return;
        uint flags = Native.SWP_NOACTIVATE;
        if (r == _placed) flags |= Native.SWP_NOMOVE | Native.SWP_NOSIZE;
        // Onto another monitor, whose DPI may differ: a small window goes there first, then takes the size. On the DPI
        // change WPF scales the window and waits for its frame, and a screen-sized transparent one hung the UI for
        // seconds (2026-10-28, a 4K player's first placement).
        else if (Native.MonitorFromPoint(new Native.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST)
                 != Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST))
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, 16, 16, Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, flags);
        _placed = r;
    }

    /// <summary>Topmost, and the game window isn't among the few above it in the Z order.</summary>
    private bool OnTopOf(IntPtr game)
    {
        if ((Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) == 0) return false;
        int seen = 0;
        for (var h = Native.GetWindow(_hwnd, Native.GW_HWNDPREV); h != IntPtr.Zero && seen < 512; h = Native.GetWindow(h, Native.GW_HWNDPREV), seen++)
            if (h == game) return false;
        return true;
    }
}
