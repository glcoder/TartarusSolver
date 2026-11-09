using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class UiHangsTests
{
    // The UI thread of hang report 630f922c (0.8.0), as the watchdog trims it.
    private static readonly string[] Held =
    [
        "MS.Win32.UnsafeNativeMethods.CallWindowProc(IntPtr, IntPtr, Int32, IntPtr, IntPtr)",
        "MS.Win32.HwndSubclass.DefWndProcWrapper(IntPtr, Int32, IntPtr, IntPtr)",
        "ILStubClass.IL_STUB_ReversePInvoke(Int64, Int32, Int64, Int64)",
        "MS.Win32.HwndSubclass.SubclassWndProc(IntPtr, Int32, IntPtr, IntPtr)",
        "MS.Win32.UnsafeNativeMethods.DispatchMessage(System.Windows.Interop.MSG ByRef)",
        "System.Windows.Threading.Dispatcher.PushFrameImpl(System.Windows.Threading.DispatcherFrame)",
        "System.Windows.Application.RunInternal(System.Windows.Window)",
        "Tartarus.App.App.Main()",
    ];

    [Fact]
    public void The_mouse_held_on_our_caption_is_not_a_hang() => Assert.True(UiHangs.HeldOnOurWindow(Held, mouseCapturedByOurs: true));

    [Fact]
    public void Without_our_window_holding_the_mouse_it_is() => Assert.False(UiHangs.HeldOnOurWindow(Held, mouseCapturedByOurs: false));

    [Fact]
    public void Our_own_code_under_it_is_a_hang() =>
        Assert.False(UiHangs.HeldOnOurWindow([.. Held[..4], "Tartarus.App.ControlWindow.PlaceOverlay()", .. Held[4..]], mouseCapturedByOurs: true));

    [Fact]
    public void Elsewhere_than_DefWindowProc_it_is() =>
        Assert.False(UiHangs.HeldOnOurWindow(["System.Windows.Media.Composition.DUCE+Channel.SyncFlush()", "Tartarus.App.App.Main()"], mouseCapturedByOurs: true));
}
