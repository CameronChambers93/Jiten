using FluentAssertions;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.User;

namespace Jiten.Tests;

public class MinedSyncTests
{
    [Fact]
    public void RenderSentencesListsEverySentenceWithMediaReferences()
    {
        var html = MinedSyncService.RenderSentences(1793133129675, [
            new MinedSentence
            {
                MinedSentenceId = 2, Text = "二つ目", SortOrder = 1, Origin = MinedSentenceOrigin.Web, SourceTitle = "news.web.nhk"
            },
            new MinedSentence
            {
                MinedSentenceId = 1, Text = "<b>しっかり</b>するんだ", Furigana = "<b>しっかり</b>するんだ", English = "Snap out of it", SortOrder = 0,
                Origin = MinedSentenceOrigin.Game, SourceTitle = "A \"Game\"", AnkiAudioFilename = "line 1.ogg",
                AnkiPictureFilename = "shot.webp"
            },
        ]);

        html.Should().StartWith("<div class=\"jiten-sentences\" data-note=\"1793133129675\"><div class=\"jiten-sentence\" data-id=\"1\" data-origin=\"game\" data-main=\"1\"");
        html.Should().Contain("data-source=\"A &quot;Game&quot;\"");
        html.Should().Contain("<div class=\"jiten-sentence-text\"><b>しっかり</b>するんだ</div>");
        html.Should().Contain("<div class=\"jiten-sentence-english\">Snap out of it</div>");
        html.Should().Contain("<audio class=\"jiten-sentence-audio\" src=\"line 1.ogg\" preload=\"none\"></audio>");
        html.Should().Contain("<img class=\"jiten-sentence-picture\" src=\"shot.webp\"");
        html.IndexOf("data-id=\"2\"", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("data-id=\"1\"", StringComparison.Ordinal));
        html.Should().EndWith("</div></div>");
    }

    [Fact]
    public void InRotationLeavesOutFlaggedSentences()
    {
        var kept = new MinedSentence { MinedSentenceId = 2, ExampleSentenceId = 20, SortOrder = 1 };
        var noOccurrence = new MinedSentence { MinedSentenceId = 3, SortOrder = 2 };
        MinedSyncService.InRotation([new MinedSentence { MinedSentenceId = 1, ExampleSentenceId = 10 }, kept, noOccurrence], new HashSet<long> { 10 })
                        .Should().Equal(kept, noOccurrence);
    }

    [Fact]
    public void InRotationKeepsTheMainSentenceWhenAllAreFlagged()
    {
        var main = new MinedSentence { ExampleSentenceId = 10, SortOrder = 0 };
        MinedSyncService.InRotation([new MinedSentence { ExampleSentenceId = 20, SortOrder = 1 }, main], new HashSet<long> { 10, 20 })
                        .Should().Equal(main);
    }

    [Fact]
    public void RenderSentencesMarksTheWordWithTheTemplate()
    {
        var html = MinedSyncService.RenderSentences(1, [
            new MinedSentence { Text = "<b>猫</b>だ", Furigana = "<b> 猫[ねこ]</b>だ", Origin = MinedSentenceOrigin.Game }
        ], "<span class=\"cloze_body\">{word}</span>");

        html.Should().Contain("<div class=\"jiten-sentence-text\"><span class=\"cloze_body\">猫</span>だ</div>");
        html.Should().Contain("<div class=\"jiten-sentence-furigana\"><span class=\"cloze_body\"> 猫[ねこ]</span>だ</div>");
    }

    [Theory]
    [InlineData("{word}", "猫だ")]
    [InlineData("<u>{word}</u>", "<u>猫</u>だ")]
    [InlineData("<b>{word}</b>", "<b>猫</b>だ")]
    [InlineData("$1{word}", "$1猫だ")]
    public void ApplyUsesTheTemplate(string template, string expected)
    {
        TargetWordMarkup.Apply("<b>猫</b>だ", template).Should().Be(expected);
    }

    [Fact]
    public void ToStoredTurnsClozeSpansIntoBold()
    {
        TargetWordMarkup.ToStored("<span class=\"cloze_body\">猫</span>と<span class=\"term\">犬</span>")
                        .Should().Be("<b>猫</b>と<span class=\"term\">犬</span>");
    }

    [Theory]
    [InlineData("<b>{word}</b>", true)]
    [InlineData("{word}", true)]
    [InlineData("<b></b>", false)]
    [InlineData("{word}{word}", false)]
    public void TemplateHoldsTheWordOnce(string template, bool valid)
    {
        TargetWordMarkup.IsValid(template).Should().Be(valid);
    }
}
