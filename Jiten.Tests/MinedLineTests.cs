using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.User;

namespace Jiten.Parser.Tests;

public class MinedLineTests
{
    private static MinedLine From(string html) => MinedLine.From(html, MinedLineStore.PlainText);

    [Fact]
    public void KeepsWhereTheNoteMarkedTheWord()
    {
        var line = From("いえ、<b>特に</b>は・・・");

        line.Should().Be(new MinedLine("いえ、特には・・・", null, 3, 2));
        line.ToHtml().Should().Be("いえ、<b>特に</b>は・・・");
    }

    [Fact]
    public void KeepsTheMarkedCopyOfARepeatedWord()
    {
        From("猫と<b>猫</b>").TargetPosition.Should().Be(2);
    }

    [Fact]
    public void AnUnmarkedLineStaysUnmarked()
    {
        var line = From("こちらこそ、よろしく。");

        line.TargetPosition.Should().BeNull();
        line.ToHtml().Should().Be("こちらこそ、よろしく。");
    }

    [Fact]
    public void MovesASpeakerOutOfTheText()
    {
        var line = From("天地「恐らく、<b>そうだろう</b>。」");

        line.Should().Be(new MinedLine("「恐らく、そうだろう。」", "天地", 5, 5));
        line.ToHtml().Should().Be("天地「恐らく、<b>そうだろう</b>。」");
    }

    [Theory]
    [InlineData("あなた「事故かな？」見る·調べる")]
    [InlineData("「どうも落ちたみたいだな」")]
    [InlineData("プロ野球のチーム「巨人」の監督が、捕まった「らしい」")]
    public void LeavesLinesThatDontFitTheSpeakerForm(string text)
    {
        From(text).Speaker.Should().BeNull();
    }
}
