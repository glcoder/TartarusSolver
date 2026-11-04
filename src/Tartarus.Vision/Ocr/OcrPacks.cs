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

    /// <summary>The game language nearest to a Windows display language, like "zh-CN" or "pt-BR".</summary>
    public static string FromCulture(string culture)
    {
        string name = culture.ToLowerInvariant();
        if (name.StartsWith("zh", StringComparison.Ordinal))
            return name.Contains("hant") || name.EndsWith("-tw", StringComparison.Ordinal) || name.EndsWith("-hk", StringComparison.Ordinal) || name.EndsWith("-mo", StringComparison.Ordinal) ? "zhTw" : "zh";
        string language = name.Split('-')[0];
        return GameLanguages.Contains(language) ? language : "en";
    }

    /// <summary>
    /// "Windows refuses to capture the game's window" in a game language: the status for players whose Windows won't capture
    /// any window. They restarted the app for hours (2026-11-03), and it's said in Windows's language, as the game's may be
    /// read wrong (clients with "en" in their settings and the game in Chinese).
    /// </summary>
    public static string CaptureRefused(string? code) => code?.ToLowerInvariant() switch
    {
        "ru" => "Нет захвата: Windows отказывается захватывать окно игры. Возможно, захват экрана отключён или удалён в этой Windows: проверьте службу CaptureService. Без него приложение не видит игру.",
        "de" => "Keine Aufnahme: Windows verweigert die Aufnahme des Spielfensters. Die Bildschirmaufnahme ist in diesem Windows vielleicht deaktiviert oder entfernt: Prüfen Sie den Dienst CaptureService. Ohne sie sieht die App das Spiel nicht.",
        "es" => "Sin captura: Windows se niega a capturar la ventana del juego. Es posible que la captura de pantalla esté desactivada o eliminada en este Windows: comprueba el servicio CaptureService. Sin ella, la aplicación no ve el juego.",
        "fr" => "Pas de capture : Windows refuse de capturer la fenêtre du jeu. La capture d'écran est peut-être désactivée ou supprimée dans ce Windows : vérifiez le service CaptureService. Sans elle, l'application ne voit pas le jeu.",
        "pt" => "Sem captura: o Windows recusa capturar a janela do jogo. A captura de tela pode estar desativada ou removida neste Windows: verifique o serviço CaptureService. Sem ela, o aplicativo não vê o jogo.",
        "id" => "Tidak menangkap: Windows menolak menangkap jendela game. Penangkapan layar mungkin dimatikan atau dihapus di Windows ini: periksa layanan CaptureService. Tanpa itu aplikasi tidak bisa melihat game.",
        "vi" => "Không chụp được: Windows từ chối chụp cửa sổ trò chơi. Có thể tính năng chụp màn hình đã bị tắt hoặc gỡ bỏ trong Windows này: hãy kiểm tra dịch vụ CaptureService. Nếu không có nó, ứng dụng không thấy được trò chơi.",
        "ja" => "キャプチャできません: Windows がゲームウィンドウのキャプチャを拒否しています。この Windows では画面キャプチャが無効化または削除されている可能性があります。CaptureService サービスを確認してください。これがないとアプリはゲームを認識できません。",
        "ko" => "캡처 불가: Windows가 게임 창 캡처를 거부합니다. 이 Windows에서 화면 캡처가 꺼졌거나 제거되었을 수 있습니다. CaptureService 서비스를 확인해 주세요. 이 기능 없이는 앱이 게임을 볼 수 없습니다.",
        "th" => "จับภาพไม่ได้: Windows ปฏิเสธการจับภาพหน้าต่างเกม การจับภาพหน้าจออาจถูกปิดหรือลบออกจาก Windows นี้ โปรดตรวจสอบบริการ CaptureService หากไม่มีบริการนี้ แอปจะมองไม่เห็นเกม",
        "zh" or "zhcn" => "未能捕获：Windows 拒绝捕获游戏窗口。此 Windows 中的屏幕捕获可能已被关闭或移除，请检查 CaptureService 服务。没有它，本程序无法看到游戏。",
        "zhtw" => "未能擷取：Windows 拒絕擷取遊戲視窗。此 Windows 中的螢幕擷取可能已被關閉或移除，請檢查 CaptureService 服務。沒有它，本程式無法看到遊戲。",
        _ => "Not capturing: Windows refuses to capture the game window. Screen capture may be turned off or removed in this Windows: check the CaptureService service. Without it the app can't see the game.",
    };

    /// <summary>The status while the game is read by a copy of the screen, in a game language.</summary>
    public static string ScreenCopyOn(string? code) => code?.ToLowerInvariant() switch
    {
        "ru" => "Копия экрана: Windows не даёт захватить окно. Держите игру на переднем плане, ничем не закрывая.",
        "de" => "Bildschirmkopie: Windows erlaubt keine Fensteraufnahme. Halten Sie das Spiel im Vordergrund und unverdeckt.",
        "es" => "Copia de pantalla: Windows no permite capturar la ventana. Mantén el juego en primer plano, sin cubrirlo.",
        "fr" => "Copie d'écran : Windows ne permet pas de capturer la fenêtre. Gardez le jeu au premier plan, sans le couvrir.",
        "pt" => "Cópia da tela: o Windows não permite capturar a janela. Mantenha o jogo em primeiro plano, sem cobri-lo.",
        "id" => "Salinan layar: Windows tidak mengizinkan penangkapan jendela. Biarkan game di depan tanpa tertutup.",
        "vi" => "Sao chép màn hình: Windows không cho chụp cửa sổ. Hãy giữ trò chơi ở trên cùng, không bị che.",
        "ja" => "画面コピー: Windows がウィンドウのキャプチャを許可していません。ゲームを前面に出し、隠さないでください。",
        "ko" => "화면 복사: Windows가 창 캡처를 허용하지 않습니다. 게임을 맨 앞에 두고 가리지 마세요.",
        "th" => "คัดลอกหน้าจอ: Windows ไม่อนุญาตให้จับภาพหน้าต่าง โปรดให้เกมอยู่ด้านหน้าและไม่ถูกบัง",
        "zh" or "zhcn" => "屏幕复制：Windows 不允许捕获窗口。请让游戏保持在最前面，不要遮挡。",
        "zhtw" => "螢幕複製：Windows 不允許擷取視窗。請讓遊戲保持在最前面，不要遮擋。",
        _ => "Screen copy: Windows won't capture the window. Keep the game in front, with nothing over it.",
    };

    /// <summary>The status while another window stands over the game and no copy of the screen is made, in a game language.</summary>
    public static string ScreenCopyCovered(string? code) => code?.ToLowerInvariant() switch
    {
        "ru" => "Подсказки ждут: игру закрывает другое окно. Уберите его.",
        "de" => "Hinweise warten: Ein anderes Fenster liegt über dem Spiel. Schieben Sie es weg.",
        "es" => "Las pistas esperan: otra ventana cubre el juego. Muévela.",
        "fr" => "Les conseils attendent : une autre fenêtre couvre le jeu. Déplacez-la.",
        "pt" => "As dicas aguardam: outra janela cobre o jogo. Mova-a.",
        "id" => "Petunjuk menunggu: jendela lain menutupi game. Pindahkan jendela itu.",
        "vi" => "Gợi ý đang chờ: một cửa sổ khác đang che trò chơi. Hãy chuyển nó đi.",
        "ja" => "ヒントは待機中です: 別のウィンドウがゲームを隠しています。移動してください。",
        "ko" => "힌트 대기 중: 다른 창이 게임을 가리고 있습니다. 옮겨 주세요.",
        "th" => "คำแนะนำกำลังรอ: มีหน้าต่างอื่นบังเกมอยู่ โปรดย้ายออก",
        "zh" or "zhcn" => "提示等待中：另一个窗口遮挡了游戏，请将其移开。",
        "zhtw" => "提示等待中：另一個視窗遮擋了遊戲，請將其移開。",
        _ => "Hints wait: another window is over the game. Move it away.",
    };

    /// <summary>Where downloaded packs go (the program's own folder may not be writable).</summary>
    public static string DownloadDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TartarusSolver", "models", "ocr");

    public static bool Exists(OcrPack pack, string dir) =>
        File.Exists(Path.Combine(dir, pack.Model)) && File.Exists(Path.Combine(dir, pack.Dictionary));

    /// <summary>The folder holding the pack: the program's models/ocr (<paramref name="modelDir"/>) or the downloads'; null when neither.</summary>
    public static string? Locate(OcrPack pack, string modelDir) =>
        Exists(pack, modelDir) ? modelDir : Exists(pack, DownloadDir) ? DownloadDir : null;
}
