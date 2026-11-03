namespace Tartarus.Vision.Ocr;

/// <summary>
/// A text line recognizer and its alphabet (PaddleOCR PP-OCRv5, ONNX): rec_&lt;id&gt;.onnx and dict_&lt;id&gt;.txt, in
/// models/ocr beside the program or, once downloaded, in %LOCALAPPDATA%\TartarusSolver\models\ocr.
/// </summary>
public sealed record OcrPack(string Id, string Script, string Folder, long ModelSize, string ModelSha256, long DictionarySize, string DictionarySha256,
    string? ModelPath = null, string? DictionaryPath = null)
{
    public string Model => $"rec_{Id}.onnx";
    public string Dictionary => $"dict_{Id}.txt";
    public long Size => ModelSize + DictionarySize;

    /// <summary>
    /// The files to download: name, its path on a host of <see cref="OcrDownload.Hosts"/>, size, SHA-256. From
    /// monkt/paddleocr-onnx's language <see cref="Folder"/> unless <see cref="ModelPath"/> and <see cref="DictionaryPath"/> say otherwise.
    /// </summary>
    public IEnumerable<(string Name, string Path, long Size, string Sha256)> Files()
    {
        string root = $"monkt/paddleocr-onnx/resolve/main/languages/{Folder}/";
        yield return (Model, ModelPath ?? root + "rec.onnx", ModelSize, ModelSha256);
        yield return (Dictionary, DictionaryPath ?? root + "dict.txt", DictionarySize, DictionarySha256);
    }
}

/// <summary>
/// Which recognizer reads which of the game's languages (its GameUserSettings.ini LanguageCode). English and East
/// Slavic and Chinese ship with the program; the others are downloaded when the game is set to one of their languages.
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
    /// <summary>
    /// Simplified and Traditional Chinese, Japanese, and English: the mobile recognizer, same alphabet as the server one
    /// (84 MB) it replaced. 2026-11-01: the server one read a Sandbox in 3 s on a TapTap player's PC and in 45-130 s on
    /// another's; the mobile one reads the same 117 report frames alike (cells, Destinations, days, cards) in half the time.
    /// </summary>
    public static readonly OcrPack Chinese = new("chinese_mobile", "CJK and Latin", "chinese",
        16517247, "bf66820f48fa99f779974c4df78e5274a9d8e0458c4137e8c5357e40e2c3faf2", 74012, "d1979e9f794c464c0d2e0b70a7fe14dd978e9dc644c0e71f14158cdf8342af1b",
        "webnn/PP-OCRv5-ONNX/resolve/main/ch_PP-OCRv5_rec.onnx", "webnn/PP-OCRv5-ONNX/resolve/main/ch_PP-OCRv5_dict.txt");
    public static readonly OcrPack Korean = new("korean", "Hangul and Latin", "korean",
        13401252, "322f140154c820fcb83c3d24cfe42c9ec70dd1a1834163306a7338136e4f1eaa", 47451, "a88071c68c01707489baa79ebe0405b7beb5cca229f4fc94cc3ef992328802d7");
    public static readonly OcrPack Thai = new("thai", "Thai and Latin", "thai",
        7873480, "2b6e56b1872200349e227574c25aeb0e0f9af9b8356e9ff5f75ac543a535669a", 1767, "57f5406f94bb6688fb7077f7be65f08bbd71cecf48c01ea26c522cb5c4836b7a");

    /// <summary>The pack for one of the game's language codes (en, ru, de, es, fr, pt, id, ja, ko, th, vi, zh, zhTw;
    /// the text tables call Simplified Chinese zhCN).</summary>
    public static OcrPack ForGame(string? languageCode) => languageCode?.ToLowerInvariant() switch
    {
        "ru" => EastSlavic,
        "de" or "es" or "fr" or "pt" or "id" or "vi" => Latin,
        "zh" or "zhcn" or "zhtw" or "ja" => Chinese,
        "ko" => Korean,
        "th" => Thai,
        _ => English,
    };

    /// <summary>The game's language by its code, in English ("ru": Russian).</summary>
    public static string LanguageName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => "English", "ru" => "Russian", "de" => "German", "es" => "Spanish", "fr" => "French", "pt" => "Portuguese",
        "id" => "Indonesian", "vi" => "Vietnamese", "ja" => "Japanese", "ko" => "Korean", "th" => "Thai",
        "zh" or "zhcn" => "Chinese (Simplified)", "zhtw" => "Chinese (Traditional)",
        _ => code ?? "?",
    };

    /// <summary>The game's languages, as its settings file writes them (Simplified Chinese "zh", seen 2026-10-31).</summary>
    public static IReadOnlyList<string> GameLanguages { get; } = ["en", "ru", "de", "es", "fr", "pt", "id", "vi", "ja", "ko", "th", "zh", "zhTw"];

    /// <summary>The language's own name for itself ("ru": Русский), for a player who doesn't read English.</summary>
    public static string NativeName(string? code) => code?.ToLowerInvariant() switch
    {
        "en" => "English", "ru" => "Русский", "de" => "Deutsch", "es" => "Español", "fr" => "Français", "pt" => "Português",
        "id" => "Bahasa Indonesia", "vi" => "Tiếng Việt", "ja" => "日本語", "ko" => "한국어", "th" => "ไทย",
        "zh" or "zhcn" => "简体中文", "zhtw" => "繁體中文",
        _ => code ?? "?",
    };

    /// <summary>"Choose the game's language" in a game language: the overlay asks in all of them, the text being unread.</summary>
    public static string ChooseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ru" => "Выберите язык игры", "de" => "Spielsprache wählen", "es" => "Elige el idioma del juego",
        "fr" => "Choisissez la langue du jeu", "pt" => "Escolha o idioma do jogo", "id" => "Pilih bahasa game",
        "vi" => "Chọn ngôn ngữ trò chơi", "ja" => "ゲームの言語を選択", "ko" => "게임 언어를 선택하세요", "th" => "เลือกภาษาของเกม",
        "zh" or "zhcn" => "请选择游戏语言", "zhtw" => "請選擇遊戲語言",
        _ => "Choose the game's language",
    };

    /// <summary>
    /// "Report only problems in Tartarus Prison" in a game language, the mode by the game's own name in it (the game's text
    /// tables, Func_S16GamePlay_Title): most reports came from other modes, the Hideout, or with the game not on screen.
    /// </summary>
    public static string ReportOnlyTartarus(string? code) => code?.ToLowerInvariant() switch
    {
        "ru" => "Сообщайте только о проблемах в режиме «Тюрьма Талоса».", "de" => "Melden Sie nur Probleme im Modus „Talos-Gefängnis“.",
        "es" => "Informa solo de problemas en el modo «Prisión de Talos».", "fr" => "Signalez uniquement les problèmes du mode « Prison de Taros ».",
        "pt" => "Relate apenas problemas no modo “Prisão de Taros”.", "id" => "Laporkan hanya masalah di mode \"Penjara Talos\".",
        "vi" => "Chỉ báo lỗi trong chế độ \"Nhà Ngục Talos\".", "ja" => "「タロス監獄」モードの問題のみ報告してください。",
        "ko" => "\"타르타로스 감옥\" 모드의 문제만 신고해 주세요.", "th" => "โปรดแจ้งเฉพาะปัญหาในโหมด \"คุกทาลอส\"",
        "zh" or "zhcn" => "请仅反馈“塔罗斯监狱”模式中的问题。", "zhtw" => "請僅回報「塔羅斯監獄」模式中的問題。",
        _ => "Report only problems in the Tartarus Prison mode.",
    };

    /// <summary>Where downloaded packs go (the program's own folder may not be writable).</summary>
    public static string DownloadDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TartarusSolver", "models", "ocr");

    public static bool Exists(OcrPack pack, string dir) =>
        File.Exists(Path.Combine(dir, pack.Model)) && File.Exists(Path.Combine(dir, pack.Dictionary));

    /// <summary>The folder holding the pack: the program's models/ocr (<paramref name="modelDir"/>) or the downloads'; null when neither.</summary>
    public static string? Locate(OcrPack pack, string modelDir) =>
        Exists(pack, modelDir) ? modelDir : Exists(pack, DownloadDir) ? DownloadDir : null;
}
