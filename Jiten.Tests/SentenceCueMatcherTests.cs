using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Media;

namespace Jiten.Tests;

public class SentenceCueMatcherTests
{
    private static readonly List<SubtitleCue> Cues =
    [
        new(1000, 2000, "（おそ松）大変だ〜！"),
        new(2100, 3000, "大変だぞ お〜い！"),
        new(3100, 4000, "（おそ松）カラ松(まつ)！\n（カラ松）あ？"),
        new(5000, 6000, "去年の3月に"),
        new(6100, 7000, "終わっちゃった"),
        new(9000, 9500, "大変だぞ お〜い！"),
    ];

    [Fact]
    public void Find_IgnoresSpeakerLabelsFuriganaPunctuationAndLongVowelMarks()
    {
        var match = new SentenceCueMatcher(Cues).Find("カラ松！", 0.3);

        match.Should().NotBeNull();
        match!.Value.First.StartMs.Should().Be(3100);
        match.Value.Last.EndMs.Should().Be(4000);
    }

    [Fact]
    public void Find_SentenceSpanningCues_ReturnsFirstAndLastCue()
    {
        var match = new SentenceCueMatcher(Cues).Find("去年の３月に終わっちゃった。", 0.6);

        match!.Value.First.StartMs.Should().Be(5000);
        match.Value.Last.EndMs.Should().Be(7000);
    }

    [Theory]
    [InlineData(0.1, 2100)]
    [InlineData(0.95, 9000)]
    public void Find_RepeatedLine_PicksTheOccurrenceNearestItsPosition(double relativePosition, int expectedStart)
    {
        var match = new SentenceCueMatcher(Cues).Find("大変だぞ、おーい！", relativePosition);

        match!.Value.First.StartMs.Should().Be(expectedStart);
    }

    [Fact]
    public void Find_UnknownSentence_ReturnsNull()
    {
        new SentenceCueMatcher(Cues).Find("知らない文です", 0.5).Should().BeNull();
    }

    [Fact]
    public void Locate_SentenceOnOneLineOfACue_GivesThatLine()
    {
        var match = new SentenceCueMatcher(Cues).Locate("あ？", 0.4);

        match!.Value.Should().Be(new CueMatch(2, 2, 0.5, 1, 1, 1));
    }

    [Fact]
    public void Locate_SentenceSpanningCues_GivesItsFirstAndLastLine()
    {
        List<SubtitleCue> cues = [new(0, 1000, "それで\n去年の3月に"), new(1100, 2000, "終わっちゃった\nえっ？")];

        var match = new SentenceCueMatcher(cues).Locate("去年の3月に終わっちゃった", 0.5);

        match!.Value.Should().Be(new CueMatch(0, 1, 0, 1, 1, 0));
    }

    [Fact]
    public void Locate_MatchedByPrefixOnly_EndsWithTheCue()
    {
        List<SubtitleCue> cues = [new(0, 1000, "そん時は驚いたよ\nほんとに近道だったし")];

        var match = new SentenceCueMatcher(cues).Locate("そん時は驚いたよね、本当に", 0.5);

        match!.Value.Should().Be(new CueMatch(0, 0, 0, 1, 0, 1));
    }

    [Fact]
    public void Lines_SkipsSpeakerLabelsOnTheirOwnLine()
    {
        SentenceCueMatcher.Lines("（トト子）\nそうなの？\nうん").Should().Equal("そうなの？", "うん");
    }
}
