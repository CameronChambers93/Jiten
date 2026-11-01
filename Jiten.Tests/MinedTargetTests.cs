using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Xunit;

namespace Jiten.Tests;

public class MinedTargetTests
{
    private static SentenceToken Token(int wordId, byte position, byte length) => new(wordId, 0, position, length, false, false);

    [Fact]
    public void A_token_covers_the_conjugated_form()
    {
        MinedTarget.Find("気が付いたか。", [Token(1591050, 0, 5)], 1591050, "気が付く").Should().Be((0, 5));
    }

    [Fact]
    public void The_first_of_two_appearances_is_used()
    {
        MinedTarget.Find("怪我と怪我", [Token(1, 0, 2), Token(1, 3, 2)], 1, "怪我").Should().Be((0, 2));
    }

    [Fact]
    public void The_written_form_is_the_fallback()
    {
        MinedTarget.Find("とても穏やかな声", [], 1183590, "穏やか").Should().Be((3, 3));
    }

    [Fact]
    public void A_word_that_is_not_there_stays_unmarked()
    {
        MinedTarget.Find("何かきっかけがあれば", [], 2236590, "書きかけ").Should().BeNull();
    }
}
