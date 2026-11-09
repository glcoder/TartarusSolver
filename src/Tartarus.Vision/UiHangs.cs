namespace Tartarus.Vision;

/// <summary>What a UI thread's stack tells of a hang the watchdog caught.</summary>
public static class UiHangs
{
    /// <summary>
    /// The player holds the mouse button down on our window's caption without moving it: Windows waits for a drag inside
    /// DefWindowProc and dispatches nothing meanwhile (2026-11-09: 4 PCs, about 8 hang reports, nothing of ours on the
    /// stack). Not a hang of ours.
    /// </summary>
    public static bool HeldOnOurWindow(IReadOnlyList<string> uiStack, bool mouseCapturedByOurs) =>
        mouseCapturedByOurs
        && uiStack.Any(l => l.StartsWith("MS.Win32.HwndSubclass.DefWndProcWrapper("))
        && uiStack.All(l => !l.StartsWith("Tartarus.") || l.StartsWith("Tartarus.App.App.Main("));
}
