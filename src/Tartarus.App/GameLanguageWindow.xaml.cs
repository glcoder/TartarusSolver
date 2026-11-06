using System.Windows;
using System.Windows.Controls;
using Tartarus.Vision.Ocr;
using Tartarus.App.Localization;

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
        // The whole window in the interface's language (Windows's by default): the player who most needs it may read no English
        // (2026-11-02: a third of those asked said "Not now", and played on hints that couldn't see the cards). The question was
        // once in each row in its own language, which loaded all 13 as the window opened. A language is named by its own name, as
        // in the interface's list: no English beside it.
        Question.Text = Strings.Language_Choose;
        Message.Text = first
            ? Strings.Language_FoundMarked
            : Loc.F(Strings.Language_Unreadable,
                OcrPacks.NativeName(current));
        foreach (var code in OcrPacks.GameLanguages)
        {
            var item = new ListBoxItem { Content = OcrPacks.NativeName(code), Tag = code };
            LanguageList.Items.Add(item);
            if (first && OcrPacks.LanguageName(code) == OcrPacks.LanguageName(current)) LanguageList.SelectedItem = item;
        }
        Loc.Apply(this);
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
