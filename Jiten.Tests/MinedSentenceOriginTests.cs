using FluentAssertions;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.User;

namespace Jiten.Tests;

public class MinedSentenceOriginTests
{
    private static ImportNoteDto Note(List<string>? tags = null, string? url = null, string? misc = null, string? audio = null, string? picture = null) =>
        new(1793133129675, "Lapis+", "Mining", tags ?? ["yomitan"], "読む", "よむ", "本を<b>読む</b>", null, null, null, url, misc, audio, null, picture, null);

    [Fact]
    public void JitenMediaNamesItsExampleSentence()
    {
        var origin = DetectedOrigin.From(Note(["jiten"], misc: "おそ松さん 第2期 Episode 15", audio: "jiten_123456.mp3"));
        origin.Origin.Should().Be(MinedSentenceOrigin.Jiten);
        origin.ExampleSentenceId.Should().Be(123456);
        origin.SourceTitle.Should().Be("おそ松さん 第2期 Episode 15");
    }

    [Fact]
    public void GsmGameTagNamesTheGame()
    {
        var origin = DetectedOrigin.From(Note(["Game::Example_Game", "GSM", "yomitan"], url: "<a href=\"http://localhost:55000/texthooker\">x</a>",
                                              audio: "line.ogg"));
        origin.Origin.Should().Be(MinedSentenceOrigin.Game);
        origin.SourceTitle.Should().Be("Example Game");
    }

    [Fact]
    public void TexthookerWithoutGameTagIsAGame()
    {
        DetectedOrigin.From(Note(url: "http://localhost:55000/texthooker")).Origin.Should().Be(MinedSentenceOrigin.Game);
        DetectedOrigin.From(Note(misc: "Texthooker")).Origin.Should().Be(MinedSentenceOrigin.Game);
    }

    [Fact]
    public void WebPageKeepsItsUrl()
    {
        var origin = DetectedOrigin.From(Note(url: "https://news.web.nhk/news/easy/ne2026/a.html"));
        origin.Origin.Should().Be(MinedSentenceOrigin.Web);
        origin.SourceTitle.Should().Be("news.web.nhk");
        origin.SourceUrl.Should().Be("https://news.web.nhk/news/easy/ne2026/a.html");
    }

    [Fact]
    public void EverythingElseIsOther()
    {
        DetectedOrigin.From(Note(url: "<a href=\"\"></a>")).Origin.Should().Be(MinedSentenceOrigin.Other);
        var mpv = DetectedOrigin.From(Note(misc: "Shoushimin_Series EP04 (08m46s485ms)", audio: "a.ogg"));
        mpv.Origin.Should().Be(MinedSentenceOrigin.Other);
        mpv.SourceTitle.Should().Be("Shoushimin_Series EP04 (08m46s485ms)");
    }

    [Fact]
    public void PlainTextDropsTagsAndEntities() =>
        MinedSentenceService.PlainText("<div>まあ&nbsp;けち</div><b>読破</b>&amp;").Should().Be("まあ けち読破&");
}
