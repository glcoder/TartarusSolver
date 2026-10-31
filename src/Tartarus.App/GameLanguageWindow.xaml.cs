using System.Windows;
using System.Windows.Controls;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

/// <summary>
/// The game's text doesn't read in the language the app took for it: asks the player which one the game shows (the
/// game's settings file may not tell: 2026-10-31, a TapTap client in Chinese with "en" in it).
/// </summary>
public partial class GameLanguageWindow : Window
{
    /// <summary>The language chosen (its game code), when the window closes with true.</summary>
    public string? Chosen { get; private set; }

    public GameLanguageWindow(string current)
    {
        InitializeComponent();
        Message.Text = $"The game's text can't be read as {OcrPacks.LanguageName(current)}: move hints may be missing or wrong. "
            + "Which language does the game show?";
        foreach (var code in OcrPacks.GameLanguages)
        {
            string name = OcrPacks.LanguageName(code), native = OcrPacks.NativeName(code);
            LanguageList.Items.Add(new ListBoxItem { Content = name == native ? name : $"{name} · {native}", Tag = code });
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
