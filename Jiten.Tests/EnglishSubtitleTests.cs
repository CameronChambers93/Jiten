using FluentAssertions;
using Jiten.Api.Services.Translation;
using Jiten.Api.Services.Translation.Subtitles;
using Jiten.Core.Data;
using Jiten.Core.Media;

namespace Jiten.Tests;

public class EnglishSubtitleTests
{
    private const string Ass = """
        [Script Info]
        Title: test

        [V4+ Styles]
        Format: Name, Fontname, Fontsize
        Style: Main,Arial,20

        [Events]
        Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
        Dialogue: 0,0:00:03.89,0:00:08.11,sign_94_1_Osomatsu_san__Re,Sign,0000,0000,0000,,Mr. Osomatsu\NReturns
        Dialogue: 0,0:00:05.62,0:00:08.11,Main - Italics,Nar,0000,0000,0000,,{\i0}Mr. Osomatsu {\i1}returns!
        Dialogue: 0,0:00:10.74,0:00:12.70,Main,Osomatsu,0000,0000,0000,,It's an emergency!
        Dialogue: 0,0:00:13.01,0:00:14.57,Main,Osomatsu,0000,0000,0000,,It's an emergency,\NI tell you!
        Dialogue: 0,0:00:15.00,0:00:16.00,Main,,0000,0000,0000,,{\p1}m 0 0 l 10 10{\p0}
        Dialogue: 0,0:00:20.00,0:00:24.00,OP,,0000,0000,0000,,La la la
        Comment: 0,0:00:30.00,0:00:31.00,Main,,0000,0000,0000,,a comment
        """;

    [Fact]
    public void ParseAss_KeepsDialogueWithoutTagsSignsSongsOrDrawings()
    {
        var cues = EnglishSubtitleParser.Parse(Ass, ".ass");

        cues.Select(c => c.Text).Should().Equal("Mr. Osomatsu returns!", "It's an emergency!", "It's an emergency, I tell you!");
        cues[0].StartMs.Should().Be(5620);
        cues[0].EndMs.Should().Be(8110);
    }

    [Fact]
    public void ParseSrt_ReadsMillisecondsAndStripsTags()
    {
        const string srt = "1\r\n00:00:01,500 --> 00:00:02,250\r\n<i>Hello</i>\r\nthere\r\n\r\n2\r\n00:01:00,000 --> 00:01:01,000\r\nBye &amp; thanks\r\n";

        var cues = EnglishSubtitleParser.Parse(srt, ".srt");

        cues.Should().Equal(new SubtitleCue(1500, 2250, "Hello there"), new SubtitleCue(60000, 61000, "Bye & thanks"));
    }

    [Fact]
    public void Aligner_JoinsCuesMostlyInsideTheSpan()
    {
        var english = new List<SubtitleCue> { new(900, 2000, "It's an emergency!"), new(2000, 3100, "I tell you!"), new(3000, 5000, "Next line.") };
        var aligner = new TranslationCueAligner(english, offsetMs: 100);

        // Japanese span 1000-3200: the first two English cues (shifted to 1000-2100 and 2100-3200) fall inside it.
        aligner.TextFor(1000, 3200).Should().Be("It's an emergency! I tell you!");
        aligner.TextFor(10_000, 11_000).Should().BeNull();
    }

    [Fact]
    public void Aligner_TakesALongCueCoveringMostOfAShortSpan()
    {
        var aligner = new TranslationCueAligner([new(0, 4000, "A long translated line.")], 0);

        aligner.TextFor(1000, 2000).Should().Be("A long translated line.");
    }

    [Fact]
    public void Aligner_SharesATwoSpeakerCueByLine()
    {
        // Osomatsu S2E15: the Japanese cue "（一松）開く！\n（おそ松）お前ならできる！" against the fansub's lines, which run later.
        var japanese = new List<SubtitleCue> { new(429_720, 431_597, "（一松）開く！\n（おそ松）お前ならできる！") };
        var english = new List<SubtitleCue> { new(428_640, 429_840, "Give it your best!"), new(429_840, 430_850, "It'll open!"), new(430_850, 433_360, "You can do it!") };
        var match = new SentenceCueMatcher(japanese).Locate("お前ならできる！。", 0.5)!.Value;
        var aligner = new TranslationCueAligner(english, 0);

        match.From.Should().Be(0.5);
        aligner.TextFor(429_720, 431_597, match.From, match.To).Should().Be("You can do it!");
        aligner.TextFor(429_720, 431_597, 0, 0.5).Should().Be("It'll open!");
        aligner.TextFor(429_720, 431_597).Should().Be("It'll open! You can do it!");
    }

    [Fact]
    public void Aligner_IgnoresSpeakerOnlyLinesWhenSharingByLine()
    {
        // Osomatsu S2E21: a speaker label on its own line, then two lines of dialogue.
        var japanese = new List<SubtitleCue> { new(1_177_593, 1_180_721, "（トト子）\nまた そっち行ったの？\n本番中よ！ ＭＣは？") };
        var english = new List<SubtitleCue>
        {
            new(1_177_480, 1_178_940, "You went over there again?!"), new(1_178_940, 1_180_760, "But the show's started! Shouldn't you be MCing?")
        };
        var match = new SentenceCueMatcher(japanese).Locate("また、そっち行ったの？", 0.5)!.Value;

        new TranslationCueAligner(english, 0).TextFor(1_177_593, 1_180_721, match.From, match.To).Should().Be("You went over there again?!");
    }

    [Fact]
    public void DetectAgainstCues_FindsTheShiftBetweenReleases()
    {
        var random = new Random(7);
        var japanese = new List<(int, int)>();
        for (int t = 5000; t < 1_300_000; t += 2500 + random.Next(4000))
            japanese.Add((t, t + 1200 + random.Next(1500)));
        // The English release is 11.3 s earlier, with slightly different cue edges.
        var english = japanese.Select(c => (c.Item1 - 11_300 + random.Next(-150, 150), c.Item2 - 11_300 + random.Next(-150, 150))).ToList();

        var result = SubtitleOffsetDetector.DetectAgainstCues(japanese, english);

        result.OffsetMs.Should().BeInRange(11_200, 11_400);
        result.Score.Should().BeGreaterThan(result.ScoreAtZero);
    }

    [Fact]
    public void Coverage_IsHighOnlyWhenShiftedCorrectly()
    {
        var japanese = new List<SubtitleCue> { new(10_000, 12_000, "a"), new(15_000, 17_000, "b") };
        var english = new List<SubtitleCue> { new(5_000, 7_000, "A"), new(10_000, 12_000, "B") };

        TranslationCueAligner.Coverage(japanese, english, 5_000).Should().Be(1);
        TranslationCueAligner.Coverage(japanese, english, 0).Should().BeLessThan(0.6);
    }

    [Theory]
    [InlineData("Mr. Osomatsu 2nd season", "おそ松さん 第2期", "Mr. Osomatsu", 2)]
    [InlineData("Mr. Osomatsu", "おそ松さん 第3期", "Mr. Osomatsu", 3)]
    [InlineData("Frieren Season 2", "葬送のフリーレン", "Frieren", 2)]
    [InlineData("Mr. Osomatsu", "おそ松さん", "Mr. Osomatsu", null)]
    public void GuessQuery_SplitsOffTheSeason(string title, string originalTitle, string query, int? season)
    {
        EnglishSubtitleService.GuessQuery(title, originalTitle).Should().Be((query, season));
    }

    [Fact]
    public void Prompt_FillsTitleContextAndSentence()
    {
        var prompt = TranslationPrompt.Render(TranslationPrompt.Default, "おそ松さん", ["大変だ！", "お前ならできる！"], "お前ならできる！");

        prompt.Should().Contain("dialogue from the show \"おそ松さん\"").And.Contain("大変だ！\nお前ならできる！")
              .And.EndWith("**Line(s) to Translate:**\nお前ならできる！");
        TranslationPrompt.Render(TranslationPrompt.Default, "x", [], "y").Should().Contain(TranslationPrompt.NoContext);
    }
}
