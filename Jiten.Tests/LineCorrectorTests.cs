using FluentAssertions;
using Jiten.Parser.SpeechBoundaries;
using Xunit;

namespace Jiten.Tests;

public class LineCorrectorTests
{
    private static (string[] Lines, List<LineCorrection> Unmatched) Apply(string[] lines, params LineCorrection[] corrections)
    {
        var (text, unmatched) = LineCorrector.Apply(string.Join('\n', lines) + "\n", corrections);
        return (text.TrimEnd('\n').Split('\n'), unmatched);
    }

    [Fact]
    public void AFix_ReplacesItsLine() =>
        Apply(["おはよう。", "今日は雨だ。", "行こう。"], new LineCorrection(1, "今日は雨だ。", "今日は飴だ。", 1))
            .Lines.Should().Equal("おはよう。", "今日は飴だ。", "行こう。");

    [Fact]
    public void ARepeatedLine_TheNearestToTheFixIsReplaced() =>
        Apply(["はい。", "そうか。", "はい。", "行こう。", "はい。"], new LineCorrection(1, "はい。", "うん。", 3))
            .Lines.Should().Equal("はい。", "そうか。", "うん。", "行こう。", "はい。");

    [Fact]
    public void TwoFixesOfTheSameText_TakeOneLineEach()
    {
        var (lines, unmatched) = Apply(["はい。", "そうか。", "はい。"],
            new LineCorrection(1, "はい。", "うん。", 0), new LineCorrection(2, "はい。", "ええ。", 2));
        lines.Should().Equal("うん。", "そうか。", "ええ。");
        unmatched.Should().BeEmpty();
    }

    [Fact]
    public void LatinTextAndPunctuation_DontStopAMatch() =>
        Apply(["皆さん、こんばんは！　Kenです！"], new LineCorrection(1, "皆さんこんばんはです", "皆さん、こんばんは！ケンです！", 0))
            .Lines.Should().Equal("皆さん、こんばんは！ケンです！");

    [Fact]
    public void WidthIsFolded() =>
        LineCorrector.Key("３月にｶﾀｶﾅ").Should().Be("3月にカタカナ");

    [Fact]
    public void AFixWhoseLineIsGone_IsReturned()
    {
        var gone = new LineCorrection(7, "もうない。", "もうないよ。", 0);
        var (lines, unmatched) = Apply(["おはよう。"], gone);
        lines.Should().Equal("おはよう。");
        unmatched.Should().Equal(gone);
    }

    [Fact]
    public void AFixWithANewline_StaysOneLine() =>
        Apply(["そん時は驚いたよ。"], new LineCorrection(1, "そん時は驚いたよ。", "そん時は\n驚いたよ。", 0))
            .Lines.Should().Equal("そん時は　驚いたよ。");

    [Fact]
    public void AFixFarFromItsLine_IsReturned()
    {
        var lines = Enumerable.Range(0, 80).Select(i => $"行{i}です。").Append("はい。").ToArray();
        var far = new LineCorrection(1, "はい。", "うん。", 0);
        Apply(lines, far).Unmatched.Should().Equal(far);
    }

    [Fact]
    public void LinesWithoutJapanese_DontCountAsPositions() =>
        Apply(["♪～", "OK", "はい。", "そうか。", "はい。"], new LineCorrection(1, "はい。", "うん。", 2))
            .Lines.Should().Equal("♪～", "OK", "はい。", "そうか。", "うん。");

    [Fact]
    public void FuriganaMarkupIsIgnoredWhenMatching() =>
        Apply(["{漢字'かんじ}を読む。"], new LineCorrection(1, "漢字を読む。", "漢字を読もう。", 0))
            .Lines.Should().Equal("漢字を読もう。");
}
