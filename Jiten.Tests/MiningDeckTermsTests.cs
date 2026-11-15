using FluentAssertions;
using Jiten.Api.Services.Mining;

namespace Jiten.Parser.Tests;

public class MiningDeckTermsTests
{
    private static readonly HashSet<string> Core = ["本", "綺麗", "元旦"];

    [Fact]
    public void AnotherWordsSpelling_DoesNotCount() =>
        // 元 (もと) lists 本 as a form, but a deck's 本 is ほん
        MiningSentenceService.IsInTerms(Core, usedForm: "元", mainForm: "元").Should().BeFalse();

    [Fact]
    public void TheFormInTheSentence_Counts() =>
        MiningSentenceService.IsInTerms(Core, usedForm: "本", mainForm: "元").Should().BeTrue();

    [Fact]
    public void TheMainForm_CountsForAKanaSpelling() =>
        MiningSentenceService.IsInTerms(Core, usedForm: "きれい", mainForm: "綺麗").Should().BeTrue();
}
