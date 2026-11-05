using System.Collections;
using FluentAssertions;
using Jiten.Parser.SpeechBoundaries;
using Xunit;

namespace Jiten.Tests;

public class CueLineAssemblerTests
{
    /// <summary>Raw lines with a blank line after each cue, and the model's sentence ends by raw line index.</summary>
    private static string[] Assemble(string[] lines, params int[] sentenceEnds)
    {
        var bits = new BitArray(lines.Length);
        foreach (var i in sentenceEnds) bits[i] = true;
        return CueLineAssembler.Assemble(lines, bits).TrimEnd('\n').Split('\n');
    }

    [Fact]
    public void TwoSentencesInOneCue_StayOneLine() =>
        Assemble(["今、夜です。今、僕は台湾のアパートにいます。", "", "今スーツケースに荷物を入れています。"])
            .Should().Equal("今、夜です。今、僕は台湾のアパートにいます。", "今スーツケースに荷物を入れています。");

    [Fact]
    public void TwoSpeakersInOneCue_AreJoinedWithAFullWidthSpace() =>
        Assemble(["そん時は驚いたよ", "ほんとに近道だったし", "", "白川さんも来るかい？"], 0, 1)
            .Should().Equal("そん時は驚いたよ　ほんとに近道だったし。", "白川さんも来るかい？");

    [Fact]
    public void ALineWrappedInsideACue_JoinsDirectly() =>
        Assemble(["俺たちもクズでバカで", "どうしようもない", ""], 1)
            .Should().Equal("俺たちもクズでバカでどうしようもない。");

    [Fact]
    public void ASentenceRunningIntoTheNextCue_JoinsTheCues() =>
        Assemble(["去年の3月に", "", "終わっちゃった", ""], 2)
            .Should().Equal("去年の3月に終わっちゃった。");

    [Fact]
    public void ACueEndingInPunctuation_EndsItsLineWhateverTheModelSays() =>
        Assemble(["見えますか？", "", "服とかお土産とかがあります。", ""])
            .Should().Equal("見えますか？", "服とかお土産とかがあります。");

    [Fact]
    public void AtMostThreeCuesJoin() =>
        Assemble(["あの", "", "えっと", "", "その", "", "つまり", ""])
            .Should().Equal("あのえっとその。", "つまり。");

    [Fact]
    public void TextWithoutCueBreaks_IsNotCueText()
    {
        CueLineAssembler.HasCueBreaks("一行目\n二行目").Should().BeFalse();
        CueLineAssembler.HasCueBreaks("一行目\n\n二行目").Should().BeTrue();
    }
}
