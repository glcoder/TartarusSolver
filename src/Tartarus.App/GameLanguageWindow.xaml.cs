using System.Windows;
using System.Windows.Controls;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

/// <summary>
/// Asks the player which language the game shows: as the app first connects to the game, or when the game's text doesn't
/// read in the language the app took for it (the game's settings file may not tell: 2026-10-31, a TapTap client in Chinese
/// with "en" in it).
/// </summary>
public partial class GameLanguageWindow : Window
{
    /// <summary>The language chosen (its game code), when the window closes with true.</summary>
    public string? Chosen { get; private set; }

    /// <param name="first">The first time: <paramref name="current"/> is the language found, marked in the list.</param>
    public GameLanguageWindow(string current, bool first = false)
    {
        InitializeComponent();
        Message.Text = first
            ? "The language found in the game's settings is marked."
            : $"The game's text can't be read as {OcrPacks.LanguageName(current)}: move hints may be missing or wrong.";
        foreach (var code in OcrPacks.GameLanguages)
        {
            string name = OcrPacks.LanguageName(code), native = OcrPacks.NativeName(code);
            // The question in each language beside it: the player who most needs this window may read no English
            // (2026-11-02: a third of those asked said "Not now", and played on hints that couldn't see the cards).
            var item = new ListBoxItem { Content = $"{(name == native ? name : $"{name} · {native}")} — {OcrPacks.ChooseLanguage(code)}", Tag = code };
            LanguageList.Items.Add(item);
            if (first && name == OcrPacks.LanguageName(current)) LanguageList.SelectedItem = item;
        }
    }

    private void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UseButton.IsEnabled = LanguageList.SelectedItem != null;

    private void LanguageList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LanguageList.SelectedItem != null) UseButton_Click(sender, e);
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        Chosen = (LanguageList.SelectedItem as ListBoxItem)?.Tag as string;
        DialogResult = Chosen != null;
    }
}
