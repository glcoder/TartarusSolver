using System.Windows;
using Tartarus.Vision.Ocr;

namespace Tartarus.App;

/// <summary>
/// The game is in a language whose OCR recognizer isn't installed: asks to download it, then shows the download's
/// progress. Declined or failed, the game's text isn't read (icons still tell the cards and picks apart).
/// </summary>
public partial class DownloadWindow : Window
{
    private readonly OcrPack _pack;
    private CancellationTokenSource? _cancel;

    /// <summary>What happened, for the app's log.</summary>
    public string Result { get; private set; } = "";

    public DownloadWindow(OcrPack pack, string languageCode)
    {
        _pack = pack;
        InitializeComponent();
        string language = OcrPacks.LanguageName(languageCode);
        Message.Text = $"The game is in {language}. Reading its text needs the {language} text recognition model "
            + $"({pack.Size / 1048576.0:0.#} MB, PaddleOCR). Download it now?\n\nWithout it the game's text won't be recognized; "
            + "cards and picks are still told apart by their pictures.";
        Result = $"OCR: {language} recognizer not downloaded, the game's text won't be read";
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        Bar.Visibility = Status.Visibility = Visibility.Visible;
        _cancel = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            Bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            Status.Text = $"{p.Done / 1048576.0:0.0} of {p.Total / 1048576.0:0.0} MB";
        });
        try
        {
            await OcrDownload.RunAsync(_pack, progress, _cancel.Token);
            _cancel = null;
            Result = $"OCR: {_pack.Id} recognizer downloaded to {OcrPacks.DownloadDir}";
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _cancel = null;
            Result = $"OCR: {_pack.Id} recognizer download cancelled, the game's text won't be read";
            DialogResult = false;
        }
        catch (Exception ex)
        {
            Result = $"OCR: {_pack.Id} recognizer download failed: {ex.Message}";
            Status.Text = "Download failed: " + ex.Message;
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = "Retry";
            CancelButton.Content = "Not now";
            _cancel = null;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closed mid-download: cancel it, and the download's end closes the window.
        if (_cancel != null)
        {
            _cancel.Cancel();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancel != null) _cancel.Cancel();
        else DialogResult = false;
    }
}
