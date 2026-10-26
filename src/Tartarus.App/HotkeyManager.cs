using System.Windows.Input;
using System.Windows.Interop;

namespace Tartarus.App;

/// <summary>
/// System-wide hotkeys (RegisterHotKey) delivered to a window of this app. While not <see cref="Active"/> they are let
/// go, so other apps get their keys (Ctrl+Shift+S, R, B... mean something in editors and browsers).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly Dictionary<int, (uint Mods, uint Key)> _keys = [];
    private int _nextId = 0x7A00;
    private bool _active = true;

    /// <summary>Whether the hotkeys are held; set as the game or this app comes to the front or leaves it.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value == _active) return;
            _active = value;
            foreach (var (id, (mods, key)) in _keys)
                if (value) Native.RegisterHotKey(_source.Handle, id, mods, key);
                else Native.UnregisterHotKey(_source.Handle, id);
        }
    }

    public HotkeyManager(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    /// <summary>Registers Ctrl+Alt+<paramref name="key"/> (or the given modifiers). False if another app owns it.</summary>
    public bool Register(Key key, Action action, ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Alt)
    {
        uint mods = Native.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (_active && !Native.RegisterHotKey(_source.Handle, id, mods, vk)) return false;
        _actions[id] = action;
        _keys[id] = (mods, vk);
        return true;
    }

    /// <summary>Registers a gesture like "Ctrl+Shift+S". False if it doesn't parse or another app owns it.</summary>
    public bool Register(string gesture, Action action)
    {
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(gesture) is not KeyGesture g) return false;
            return Register(g.Key, action, g.Modifiers);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _keys.Clear();
        _source.RemoveHook(WndProc);
    }
}
