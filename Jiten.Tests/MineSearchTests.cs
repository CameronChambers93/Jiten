using FluentAssertions;
using Jiten.Api.Services.Mining;

namespace Jiten.Tests;

/// <summary>Cleaning saved Mine searches: names, ids, allowed query keys and run settings.</summary>
public class MineSearchTests
{
    private static MineSearchDto Search(string name, Dictionary<string, string>? query = null, string? id = null, MineSearchRunDto? run = null) =>
        new(id, name, query ?? new Dictionary<string, string> { ["sort"] = "frequency" }, run);

    [Fact]
    public void Clean_KeepsAllowedKeys_AndDropsUnknownBlankAndLongValues()
    {
        var clean = MineSearchService.Clean([
            Search("Bunny Drop", new Dictionary<string, string>
            {
                ["titles"] = "1,2", ["deck"] = "Core 2000", ["lmin"] = "16",
                ["page"] = "3", ["fmin"] = " ", ["types"] = new string('x', 501)
            })
        ]);

        clean[0].Query.Should().BeEquivalentTo(new Dictionary<string, string> { ["titles"] = "1,2", ["deck"] = "Core 2000", ["lmin"] = "16" });
        clean[0].Id.Should().NotBeNullOrWhiteSpace("a missing id is made");
    }

    [Fact]
    public void Clean_TrimsNames_AndRefusesBlankLongAndDuplicateNames()
    {
        MineSearchService.Clean([Search("  Core  ")])[0].Name.Should().Be("Core");
        FluentActions.Invoking(() => MineSearchService.Clean([Search(" ")])).Should().Throw<MineSearchException>();
        FluentActions.Invoking(() => MineSearchService.Clean([Search(new string('a', 41))])).Should().Throw<MineSearchException>();
        FluentActions.Invoking(() => MineSearchService.Clean([Search("Core"), Search("core")]))
                     .Should().Throw<MineSearchException>().WithMessage("*called*");
    }

    [Fact]
    public void Clean_RefusesDuplicateIds_AndMoreThanFifty()
    {
        FluentActions.Invoking(() => MineSearchService.Clean([Search("A", id: "x"), Search("B", id: "x")]))
                     .Should().Throw<MineSearchException>().WithMessage("*same id*");
        FluentActions.Invoking(() => MineSearchService.Clean([Search("A", id: new string('x', 101))])).Should().Throw<MineSearchException>();
        MineSearchService.Clean([Search("A", run: new MineSearchRunDto(new string('p', 101)))])[0].Run!.PipelineId.Should().BeNull();
        FluentActions.Invoking(() => MineSearchService.Clean(Enumerable.Range(0, 51).Select(i => Search($"S{i}")).ToList()))
                     .Should().Throw<MineSearchException>();
    }

    [Fact]
    public void Clean_ClampsRunSettings()
    {
        var clean = MineSearchService.Clean([
            Search("A", run: new MineSearchRunDto(" p1 ", 9, "maybe")),
            Search("B", run: new MineSearchRunDto("", 0, "skip")),
            Search("C")
        ]);

        clean[0].Run.Should().Be(new MineSearchRunDto("p1", 5, "existing"));
        clean[1].Run.Should().Be(new MineSearchRunDto(null, 1, "skip"));
        clean[2].Run.Should().BeNull();
    }
}
