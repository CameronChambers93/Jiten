using FluentAssertions;
using Jiten.Core.Data.User;

namespace Jiten.Tests;

public class MinedLineMoveTests
{
    [Fact]
    public void TheWordStaysMarkedWhereItIsInTheFixedText()
    {
        var line = new MinedLine("えっ、なに、その頭！", null, 8, 1).MovedTo("えっ、なに、その頭！", "ええっ、なに、その頭！");
        line.ToHtml().Should().Be("ええっ、なに、その<b>頭</b>！");
    }

    [Fact]
    public void AWordTheFixRemovedIsNoLongerMarked()
    {
        var line = new MinedLine("えっ、なに、その頭！", "（トド松）", 8, 1).MovedTo("えっ、なに、その頭！", "えっ、なに、その髪！");
        line.ToHtml().Should().Be("（トド松）えっ、なに、その髪！");
    }
}
