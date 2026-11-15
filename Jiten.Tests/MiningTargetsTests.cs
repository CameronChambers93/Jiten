using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.User;

namespace Jiten.Parser.Tests;

public class MiningTargetsTests
{
    private const int Unknown = 1000001, Fresh = 1000002, Younger = 1000003, Older = 1000004, Done = 1000005;

    private static readonly Dictionary<int, MiningStatus> Statuses = new()
    {
        [Key(Unknown)] = MiningStatus.New,
        [Key(Fresh)] = MiningStatus.Learning,
        [Key(Younger)] = MiningStatus.Young,
        [Key(Older)] = MiningStatus.Mature,
        [Key(Done)] = MiningStatus.Settled
    };

    private static int Key(int wordId) => ExampleSentenceTokens.WordKey(wordId, 0);

    private static SentenceToken Token(int wordId, byte position = 0, bool function = false) => new(wordId, 0, position, 1, false, function);

    private static MiningRule Rule(bool othersMature, params MiningStatus[] targets) => new(targets.ToHashSet(), othersMature);

    private static List<int> Targets(MiningRule rule, params SentenceToken[] tokens) =>
        SentenceComprehension.MiningTargets(tokens, k => Statuses.GetValueOrDefault(k, MiningStatus.New), rule).Select(t => t.Token.WordId).ToList();

    [Theory]
    [InlineData(MiningStatus.New)]
    [InlineData(MiningStatus.Learning, KnownState.Due)]
    [InlineData(MiningStatus.Learning, KnownState.Suspended)]
    [InlineData(MiningStatus.Young, KnownState.Due, KnownState.Young)]
    [InlineData(MiningStatus.Mature, KnownState.Mature, KnownState.Suspended)]
    [InlineData(MiningStatus.Settled, KnownState.Blacklisted)]
    [InlineData(MiningStatus.Settled, KnownState.Redundant)]
    public void StatusComesFromTheWordsStates(MiningStatus expected, params KnownState[] states) =>
        SentenceComprehension.MiningStatusOf(states).Should().Be(expected);

    [Fact]
    public void TheDefaultRuleIsIPlusOne()
    {
        Targets(MiningRule.Default, Token(Unknown), Token(Younger), Token(Older)).Should().Equal(Unknown);
        Targets(MiningRule.Default, Token(Unknown), Token(Fresh)).Should().BeEmpty("two words are unknown");
        Targets(MiningRule.Default, Token(Younger), Token(Older)).Should().BeEmpty("nothing is unknown");
    }

    [Fact]
    public void MatureOnlyMakesAYoungWordCountAgainstTheSentence()
    {
        Targets(Rule(true, MiningStatus.New), Token(Unknown), Token(Older)).Should().Equal(Unknown);
        Targets(Rule(true, MiningStatus.New), Token(Unknown), Token(Younger)).Should().BeEmpty();
        Targets(Rule(true, MiningStatus.Young), Token(Younger), Token(Older)).Should().Equal(Younger);
    }

    [Fact]
    public void ASentenceOfKnownWordsGivesOneTargetPerWantedWord()
    {
        Targets(Rule(false, MiningStatus.Young, MiningStatus.Mature), Token(Younger), Token(Older), Token(Done)).Should().Equal(Younger, Older);
    }

    [Fact]
    public void AnUnwantedWeakWordBlocksTheOthers()
    {
        Targets(Rule(false, MiningStatus.Mature), Token(Unknown), Token(Older)).Should().BeEmpty();
    }

    [Fact]
    public void ALooseRuleMakesEachWeakWordATarget()
    {
        var loose = MiningRule.Default with { MaxWeak = 2 };

        Targets(loose, Token(Unknown), Token(Fresh), Token(Older)).Should().Equal(Unknown, Fresh);
        Targets(loose with { Targets = new HashSet<MiningStatus> { MiningStatus.New } }, Token(Unknown), Token(Fresh)).Should().Equal(Unknown);
        Targets(loose, Token(Unknown), Token(Older)).Should().Equal(Unknown);
        Targets(loose, Token(Unknown), Token(Unknown + 10), Token(Fresh)).Should().BeEmpty("three weak words is over the cap");
    }

    [Fact]
    public void UnderALooseRuleAYoungWordMustStillBeTheOnlyWeakOneWhenOthersMustBeMature()
    {
        var loose = Rule(true, MiningStatus.New, MiningStatus.Young) with { MaxWeak = 5 };

        Targets(loose, Token(Unknown), Token(Younger)).Should().BeEmpty();
        Targets(loose, Token(Younger), Token(Older)).Should().Equal(Younger);
        Targets(loose, Token(Unknown), Token(Unknown + 10), Token(Older)).Should().Equal(Unknown, Unknown + 10);
    }

    [Fact]
    public void TheCacheKeyTellsTheCapsApart() =>
        MiningRule.Default.CacheKey.Should().NotBe((MiningRule.Default with { MaxWeak = MiningRule.LooseMaxWeak }).CacheKey);

    [Fact]
    public void CountsAreDistinctContentWordsWithTheTarget()
    {
        var tokens = new[] { Token(Unknown), Token(Unknown), Token(Unknown + 10), Token(Fresh), Token(Fresh + 10, function: true), Token(5_000_001), Token(Older) };

        SentenceComprehension.CountByStatus(tokens, k => Statuses.GetValueOrDefault(k, MiningStatus.New)).Should().Be((2, 1));
    }

    [Fact]
    public void FunctionWordsAndNamesDontCountAndARepeatedWordKeepsItsFirstPosition()
    {
        var name = Token(5_000_001);
        var function = Token(Fresh, function: true);

        var targets = SentenceComprehension.MiningTargets([name, function, Token(Unknown, 3), Token(Unknown, 7)], k => Statuses[k], MiningRule.Default);

        targets.Should().ContainSingle().Which.Token.Position.Should().Be(3);
    }
}
