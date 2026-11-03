using FluentAssertions;
using Jiten.Api.Services.Mining;

namespace Jiten.Tests;

public class WordMatchTests
{
    // JMdict 1049000 コート "coat" and 2842174 コート "court", as the dictionary holds them.
    private static readonly IReadOnlyList<string> Coat = ["coat", "coating"];
    private static readonly IReadOnlyList<string> Court = ["court (e.g. tennis, basketball, volleyball)"];

    private static int? Decide(string? text) => WordMatchService.Decide([Coat, Court], text);

    [Fact]
    public void TheGlossOnTheNoteDecides()
    {
        Decide("コート<br>court (tennis)<br>新しい<b>コート</b>でテニスをしました。<br>I played tennis on the new court.[sound:a.mp3]").Should().Be(1);
        Decide("コート<div>coat</div>寒かったのでコートを着た。 I put on a coat because it was cold.").Should().Be(0);
    }

    [Fact]
    public void BothOrNeitherIsAGuess()
    {
        Decide("1. coat 2. court").Should().BeNull();
        Decide("コート 新しいコート").Should().BeNull();
        Decide(null).Should().BeNull();
    }

    [Fact]
    public void WholeWordsOnly()
    {
        // "courtyard" and "coats" aren't the glosses.
        Decide("courtyard").Should().BeNull();
        Decide("Courtyard; coat.").Should().Be(0);
    }

    [Fact]
    public void GlossesEveryCandidateHasDecideNothing()
    {
        WordMatchService.Decide([["good", "skillful"], ["good", "delicious"]], "good").Should().BeNull();
        WordMatchService.Decide([["good", "skillful"], ["good", "delicious"]], "good, delicious").Should().Be(1);
    }

    [Fact]
    public void GlossesUnderThreeLettersDontDecide()
    {
        // "go" is in any sentence about going; it mustn't make a confident match.
        WordMatchService.Decide([["to go"], ["to be"]], "Let's go.").Should().BeNull();
    }

    [Fact]
    public void MarkupSoundTagsAndFuriganaAreNotWords()
    {
        WordMatchService.NormaliseText("新[あたら]しい<b>Court</b>&nbsp;[sound:court.mp3]").Should().Be("新 しい court");
        WordMatchService.NormaliseGloss("to eat (food)").Should().Be("eat");
    }
}
