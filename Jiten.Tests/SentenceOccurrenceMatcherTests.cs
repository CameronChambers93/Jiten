using FluentAssertions;
using Jiten.Core.Data;
using static Jiten.Core.Data.SentenceOccurrenceMatcher;

namespace Jiten.Parser.Tests;

public class SentenceOccurrenceMatcherTests
{
    [Fact]
    public void SameTextKeepsItsRowWhenItsPositionMoves()
    {
        Match([new Stored(10, 1, 5)], [new Incoming(1, 8)]).Should().Equal(10L);
    }

    [Fact]
    public void NewTextHasNoRow()
    {
        Match([new Stored(10, 1, 5)], [new Incoming(2, 5)]).Should().Equal((long?)null);
    }

    [Fact]
    public void RepeatedLinePairsNearestPositionsFirst()
    {
        Match([new Stored(10, 1, 0), new Stored(11, 1, 100)], [new Incoming(1, 98), new Incoming(1, 3)])
            .Should().Equal(11L, 10L);
    }

    [Fact]
    public void RepeatedLineWithFewerStoredRowsLeavesTheFurthestNew()
    {
        Match([new Stored(10, 1, 50)], [new Incoming(1, 0), new Incoming(1, 49)])
            .Should().Equal(null, 10L);
    }

    [Fact]
    public void AStoredRowIsTakenOnce()
    {
        var matches = Match([new Stored(10, 1, 0), new Stored(11, 1, 1)], [new Incoming(1, 0), new Incoming(1, 0), new Incoming(1, 0)]);

        matches.Should().Contain(10L).And.Contain(11L).And.Contain((long?)null);
    }
}
