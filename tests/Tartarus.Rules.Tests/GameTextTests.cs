using Tartarus.Vision;
using Tartarus.Vision.Ocr;

namespace Tartarus.Rules.Tests;

/// <summary>Reading the screen in another language than English: OCR's lines matched to the game's own texts.</summary>
public class GameTextTests
{
    [Fact]
    public void TextsAreThere() => Assert.True(GameText.Loaded, "models/text/tartarus_text.json");

    [Fact]
    public void The_Brass_Horn_reads_in_Simplified_Chinese_as_the_card_shows_it()
    {
        // 2026-11-01: the text table has "升阶奖励" for it, the card "斗争铜号".
        var (treasure, score) = WardenTreasures.Match("斗争铜号");
        Assert.Equal(("horn", 1.0), (treasure.Id, score));
    }

    [Theory]
    // As the East Slavic recognizer read them off Russian frames: spaces dropped, a letter off.
    [InlineData("Крюкотбора", "hook")]
    [InlineData("Шипастая железная плеть", "whip")]
    [InlineData("Палаческая секира надзирателя", "axe")]
    [InlineData("Столб для сожжения", "stake")]
    [InlineData("Жeтон каторжника", "dogtag")]
    [InlineData("Sorting Hook", "hook")]
    public void InstrumentNamesInAnyLanguage(string read, string id)
    {
        var (instrument, score) = TortureInstruments.Match(read);
        Assert.Equal(id, instrument.Id);
        Assert.True(score >= LayoutScanner.PickNameScore, $"{read}: {score:0.00}");
    }

    [Theory]
    [InlineData("Злоупотребление властью", "tyranny")]
    [InlineData("Железный закон", "ironlaw")]
    public void ThemeNames(string read, string id) => Assert.Equal(id, RunThemes.Match(read).Theme.Id);

    [Theory]
    [InlineData("Пожирание", "consume")]
    [InlineData("Заменить", "replace")]
    [InlineData("Завершить", "end")]
    [InlineData("Consume", "consume")]
    [InlineData("Пойти в №3.", null)]
    public void DecisionWords(string read, string? kind) => Assert.Equal(kind, ScreenText.WordKind(read));

    [Fact]
    public void RussianScreen()
    {
        var text = Parse(("title", "Выберите цель"), ("cards", "Пойти в №3."), ("cards", "Пойти вдопросную"),
            ("objective", "Цель: Закон джунглей"), ("objective", "Пожирание2/4"), ("days", "До бунта осталось 16 дн."));
        Assert.True(text.RatwayPick);
        Assert.Equal(16, text.Days);
        Assert.Equal((2, 4), text.Progress);
        Assert.Equal(Rules.ObjectiveKind.SurvivalOfTheFittest, GameObservation.Objective(text));
        Assert.Equal([3, 1], text.Destinations.Select(d => d ?? 0));
    }

    [Fact]
    public void ConsumeBanner() => Assert.True(Parse(("banner", "Выберите узника для пожирания")).ConsumePick);

    [Fact]
    public void A_pick_is_not_the_Sewer_Ratway_the_last_Sandbox_text_told_of()
    {
        // 2026-10-28: the Hunt's "Select an Objective" was the last Sandbox text before a Warden's Treasure, whose hint
        // became a Sewer Ratway's cell (no card to frame).
        var text = Parse(("title", "Select an Objective"));
        Assert.True(text.RatwayPick);
        var under = EditableSandbox.Empty();
        under.Warden = 7;
        var pick = new PickRead { Kind = PickRead.Treasure, Cards = [new PickCardRead { Index = 1, Id = "vintage" }] };
        var o = GameObservation.FromPick(pick, under, text);
        Assert.False(o.Ratway);
        Assert.Single(o.Treasures);
    }

    private static (ScreenText Text, ScreenLayout Layout) Row(params (double Middle, string Kind)[] read)
    {
        var layout = new ScreenLayout(1920, 1200);
        var byRole = ScreenLayout.TextRegions.ToDictionary(r => r.Role, _ => new List<TextLine>());
        foreach (var (middle, _) in read)
        {
            int x = (int)Math.Round(960 + middle * layout.K);
            var box = new PixelBox(x - 40, 800, x + 40, 824);
            byRole["cards"].Add(new TextLine { Text = "?", Box = box, Ink = box, Conf = 1, DetScore = 1, Chars = [] });
        }
        var text = ScreenText.Parse(byRole, layout);
        text.ApplyCardKinds(layout, cx => read.MinBy(r => Math.Abs(r.Middle - cx)).Kind);
        return (text, layout);
    }

    private const double Pitch = ScreenLayout.CardPitch;

    [Theory]
    [InlineData("2f", 2)] // Chinese cards read as English
    [InlineData("$4\\f$", 4)]
    [InlineData("7f", 7)]
    [InlineData("Go to Cell 9", 9)]
    [InlineData("122", null)] // no cell
    [InlineData("1", null)]
    [InlineData("3 12", 12)] // the number at the end still goes first
    [InlineData("3 4f", null)] // two numbers, none at the end: no telling
    [InlineData("f", null)]
    public void A_Destination_s_number_is_the_card_s_lone_number(string read, int? cell)
    {
        var text = Parse(("cards", read));
        text.ApplyCardKinds(new ScreenLayout(1920, 1200), _ => "cell");
        Assert.Equal(cell, text.Cards[0].DestinationCell);
    }

    [Fact]
    public void The_log_is_written_in_order_and_the_caller_does_not_wait_for_the_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "tartarus-log-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using var log = new BackgroundLog(path);
            log.Write("one");
            Assert.True(log.Flush(5000));
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) log.Write("line " + i);
                Assert.True(clock.ElapsedMilliseconds < 200, $"{clock.ElapsedMilliseconds} ms");
                // The file is held: nothing reaches it, and waiting gives up.
                Assert.False(log.Flush(100));
            }
            Assert.True(log.Flush(10000));
            Assert.Equal(new[] { "one" }.Concat(Enumerable.Range(0, 100).Select(i => "line " + i)), File.ReadAllLines(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_refused_capture_is_told_in_each_of_the_game_s_languages()
    {
        var lines = OcrPacks.GameLanguages.Select(OcrPacks.CaptureRefused).ToList();
        Assert.All(lines, l => Assert.Contains("CaptureService", l));
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public void The_screen_copy_statuses_are_told_in_each_of_the_game_s_languages()
    {
        foreach (var text in new Func<string?, string>[] { OcrPacks.ScreenCopyOn, OcrPacks.ScreenCopyCovered })
        {
            var lines = OcrPacks.GameLanguages.Select(text).ToList();
            Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
            Assert.Equal(lines.Count, lines.Distinct().Count());
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hans-CN", "zh")]
    [InlineData("zh-TW", "zhTw")]
    [InlineData("zh-Hant-HK", "zhTw")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ko-KR", "ko")]
    [InlineData("pl-PL", "en")]
    [InlineData("", "en")]
    public void Windows_s_language_gives_the_game_language_nearest_to_it(string culture, string code) =>
        Assert.Equal(code, OcrPacks.FromCulture(culture));

    [Fact]
    public void The_report_window_says_where_to_report_in_each_of_the_game_s_languages()
    {
        var lines = OcrPacks.GameLanguages.Select(OcrPacks.ReportOnlyTartarus).ToList();
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public void The_language_is_asked_for_in_each_of_the_game_s_languages()
    {
        var lines = OcrPacks.GameLanguages.Select(OcrPacks.ChooseLanguage).ToList();
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(lines.Count, lines.Distinct().Count());
    }

    [Fact]
    public void End_alone_in_the_middle_is_the_whole_row_and_needs_no_looking()
    {
        var (text, layout) = Row((0, "end"));
        text.AddUnreadCards(layout, _ => throw new InvalidOperationException("looked"));
        Assert.Equal(["end"], text.Cards.Select(c => c.Meaning));
    }

    [Fact]
    public void Cards_left_of_End_are_told_by_their_icons()
    {
        var (text, layout) = Row((Pitch, "end"));
        text.AddUnreadCards(layout, x => x < 0 ? "consume" : "replace");
        Assert.Equal(["consume", "replace", "end"], text.Cards.Select(c => c.Meaning));
    }

    [Fact]
    public void A_card_left_of_End_stands_there_even_if_its_icon_does_not_read()
    {
        var (text, layout) = Row((Pitch / 2, "end"));
        text.AddUnreadCards(layout, _ => null);
        Assert.Equal([ScreenText.UnreadCard, "end"], text.Cards.Select(c => c.Meaning));
    }

    [Fact]
    public void A_card_read_is_not_looked_for_again_and_the_row_of_four_is_counted()
    {
        var (text, layout) = Row((-Pitch / 2, "replace"), (1.5 * Pitch, "end"));
        var asked = new List<double>();
        text.AddUnreadCards(layout, x => { asked.Add(x); return x < -Pitch ? "consume" : "hunt"; });
        Assert.Equal([Pitch / 2, -1.5 * Pitch], asked);
        Assert.Equal(["consume", "replace", "hunt", "end"], text.Cards.Select(c => c.Meaning));
    }

    [Fact]
    public void Without_End_only_an_icon_adds_a_card()
    {
        var (text, layout) = Row((0, "replace"));
        text.AddUnreadCards(layout, x => x == Pitch ? "end" : null);
        Assert.Equal(["replace", "end"], text.Cards.Select(c => c.Meaning));
    }

    [Fact]
    public void Destination_cards_are_left_alone()
    {
        var (text, layout) = Row((-Pitch, "cell"), (0, "cell"), (Pitch, "cell"));
        text.AddUnreadCards(layout, _ => throw new InvalidOperationException("looked"));
        Assert.Equal(3, text.Cards.Count);
    }

    /// <summary>Lines as OCR gives them, by region, laid out on a 1920x1200 screen (cards left to right).</summary>
    private static ScreenText Parse(params (string Role, string Text)[] lines)
    {
        var layout = new ScreenLayout(1920, 1200);
        var byRole = ScreenLayout.TextRegions.ToDictionary(r => r.Role, _ => new List<TextLine>());
        int card = 0;
        foreach (var (role, text) in lines)
        {
            int x = role == "cards" ? 960 + (card++ - 1) * 345 - 70 : 100;
            var box = new PixelBox(x, 100 + 30 * byRole[role].Count, x + 140, 124 + 30 * byRole[role].Count);
            byRole[role].Add(new TextLine { Text = text, Box = box, Ink = box, Conf = 1, DetScore = 1, Chars = [] });
        }
        return ScreenText.Parse(byRole, layout);
    }
}
