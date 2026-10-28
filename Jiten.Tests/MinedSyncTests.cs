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
                IsMain = true, Origin = MinedSentenceOrigin.Game, SourceTitle = "A \"Game\"", AnkiAudioFilename = "line 1.ogg",
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
}
