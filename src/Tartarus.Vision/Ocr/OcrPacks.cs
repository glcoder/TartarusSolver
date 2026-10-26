namespace Tartarus.Vision.Ocr;

/// <summary>
/// A text line recognizer and its alphabet (PaddleOCR PP-OCRv5, ONNX): rec_&lt;id&gt;.onnx and dict_&lt;id&gt;.txt, in
/// models/ocr beside the program or, once downloaded, in %LOCALAPPDATA%\TartarusSolver\models\ocr.
/// </summary>
public sealed record OcrPack(string Id, string Script, string Folder, long ModelSize, string ModelSha256, long DictionarySize, string DictionarySha256)
{
    public string Model => $"rec_{Id}.onnx";
    public string Dictionary => $"dict_{Id}.txt";
    public long Size => ModelSize + DictionarySize;

    /// <summary>The files to download: name, address, size, SHA-256.</summary>
    public IEnumerable<(string Name, string Url, long Size, string Sha256)> Files()
    {
        string root = $"https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages/{Folder}/";
        yield return (Model, root + "rec.onnx", ModelSize, ModelSha256);
        yield return (Dictionary, root + "dict.txt", DictionarySize, DictionarySha256);
    }
}

/// <summary>
/// Which recognizer reads which of the game's languages (its GameUserSettings.ini LanguageCode). English and East
/// Slavic ship with the program; the others are downloaded when the game is set to one of their languages.
/// </summary>
public static class OcrPacks
{
    public static readonly OcrPack English = new("en", "Latin", "english",
        7830888, "4e16deb22c4da6468bdca539b2cd3c8687825538b67109177c47d359ab994cd7", 1416, "e025a66d31f327ba0c232e03f407ae8d105e1e709e7ccb3f408aa778c24e70d6");
    /// <summary>Russian, Ukrainian, Belarusian, Bulgarian, and English: Cyrillic and Latin letters.</summary>
    public static readonly OcrPack EastSlavic = new("eslav", "Cyrillic and Latin", "eslav",
        7870092, "dc6bf0e855247decce214ba6dae5bc135fa0ad725a5918a7fcfb59fad6c9cdee", 1663, "3e95f1581557162870cacdba5af91a4c6be2890710d395b0c3c7578e7ee5e6eb");
    /// <summary>French, German, Spanish, Portuguese, Indonesian, Vietnamese and other Latin-script languages, with their accents.</summary>
    public static readonly OcrPack Latin = new("latin", "Latin with accents", "latin",
        7862832, "614ffc2d6d3902d360fad7f1b0dd455ee45e877069d14c4e51a99dc4ef144409", 1634, "3c0a8a79b612653c25f765271714f71281e4e955962c153e272b7b8c1d2b13ff");
    /// <summary>Simplified and Traditional Chinese, Japanese, and English (the server recognizer).</summary>
    public static readonly OcrPack Chinese = new("chinese", "CJK and Latin", "chinese",
        84468836, "26fa4f47060f58e25962b9af6beaee05c8182b90e026c4ecc6db165d9dfdc38a", 74012, "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b");
    public static readonly OcrPack Korean = new("korean", "Hangul and Latin", "korean",
        13401252, "322f140154c820fcb83c3d24cfe42c9ec70dd1a1834163306a7338136e4f1eaa", 47451, "a88071c68c01707489baa79ebe0405b7beb5cca229f4fc94cc3ef992328802d7");
    public static readonly OcrPack Thai = new("thai", "Thai and Latin", "thai",
        7873480, "2b6e56b1872200349e227574c25aeb0e0f9af9b8356e9ff5f75ac543a535669a", 1767, "57f5406f94bb6688fb7077f7be65f08bbd71cecf48c01ea26c522cb5c4836b7a");

    /// <summary>The pack for one of the game's language codes (en, ru, de, es, fr, pt, id, ja, ko, th, vi, zhCN, zhTw).</summary>
    public static OcrPack ForGame(string? languageCode) => languageCode?.ToLowerInvariant() switch
    {
        "ru" => EastSlavic,
        "de" or "es" or "fr" or "pt" or "id" or "vi" => Latin,
        "zhcn" or "zhtw" or "ja" => Chinese,
        "ko" => Korean,
        "th" => Thai,
        _ => English,
    };

    /// <summary>The game's language by its code, in English ("ru": Russian).</summary>
    public static string LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => "English", "ru" => "Russian", "de" => "German", "es" => "Spanish", "fr" => "French", "pt" => "Portuguese",
        "id" => "Indonesian", "vi" => "Vietnamese", "ja" => "Japanese", "ko" => "Korean", "th" => "Thai",
        "zhcn" => "Chinese (Simplified)", "zhtw" => "Chinese (Traditional)",
        _ => code ?? "?",
    };

    /// <summary>Where downloaded packs go (the program's own folder may not be writable).</summary>
    public static string DownloadDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TartarusSolver", "models", "ocr");

    public static bool Exists(OcrPack pack, string dir) =>
        File.Exists(Path.Combine(dir, pack.Model)) && File.Exists(Path.Combine(dir, pack.Dictionary));

    /// <summary>The folder holding the pack: the program's models/ocr (<paramref name="modelDir"/>) or the downloads'; null when neither.</summary>
    public static string? Locate(OcrPack pack, string modelDir) =>
        Exists(pack, modelDir) ? modelDir : Exists(pack, DownloadDir) ? DownloadDir : null;
}
