namespace Tartarus.Vision;

/// <summary>A top-level window as Windows lists it.</summary>
public readonly record struct WindowSeen(string Process, string Title, bool Visible, bool Cloaked, bool ToolWindow, bool Minimized);

/// <summary>
/// What a bug report says of the desktop when no game is attached: the game's windows whole, of the others only the
/// visible ones' process names (their titles may be private).
/// </summary>
public static class WindowsSeen
{
    public static string Describe(IEnumerable<WindowSeen> windows)
    {
        var all = windows.ToList();
        var lines = all.Where(IsGame).Select(w => $"{w.Process} \"{w.Title}\"{Flag(w.Visible, " visible")}{Flag(w.Cloaked, " cloaked")}{Flag(w.ToolWindow, " tool")}{Flag(w.Minimized, " minimized")}").ToList();
        if (lines.Count == 0) lines.Add("No window of the game");
        var others = all.Where(w => w.Visible && !w.Cloaked && !IsGame(w)).GroupBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
        lines.Add("Other visible windows: " + string.Join(", ", others));
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsGame(WindowSeen w) =>
        w.Process.Contains("torchlight", StringComparison.OrdinalIgnoreCase)
        || w.Title.Contains("torchlight", StringComparison.OrdinalIgnoreCase)
        || w.Title.Contains("火炬"); // the game's Chinese name starts so

    private static string Flag(bool on, string name) => on ? name : "";
}
