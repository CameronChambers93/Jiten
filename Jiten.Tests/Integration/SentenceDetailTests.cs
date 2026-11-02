using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SentenceDetailTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Sentences.ExecuteDeleteAsync();
        db.Decks.AddRange(new Deck { DeckId = 1, OriginalTitle = "One", MediaType = MediaType.Anime },
                          new Deck { DeckId = 2, OriginalTitle = "Two", MediaType = MediaType.Anime },
                          new Deck { DeckId = 3, OriginalTitle = "Three", MediaType = MediaType.Anime });
        db.DeckVideoSources.Add(new DeckVideoSource { DeckId = 1, VideoPath = "/media/one.mkv" });
        db.Sentences.Add(new Sentence { Id = 10, TextHash = [1], Text = "同じ行" });
        db.ExampleSentences.AddRange(
            new ExampleSentence { SentenceId = 1, DeckId = 1, Text = "同じ行", Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 2, DeckId = 2, Text = "同じ行", Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 3, Text = "同じ行", Tokens = NoWords, WordKeys = [], CentralSentenceId = 10, OwnerId = TestUsers.UserB },
            new ExampleSentence { SentenceId = 4, DeckId = 3, Text = "別の行", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 5, DeckId = 3, Text = "また別の行", Tokens = NoWords, WordKeys = [] });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Get(long id) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/sentences/{id}").WithUser(TestUsers.UserA));

    [Fact]
    public async Task ShowsTheSentenceAndItsOtherVisibleOccurrences()
    {
        var response = await Get(1);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("sentence").GetProperty("hasVideo").GetBoolean().Should().BeTrue();
        body.GetProperty("elsewhere").EnumerateArray().Select(s => s.GetProperty("sentenceId").GetInt64()).Should().Equal(2);
    }

    [Fact]
    public async Task SentencesWithoutACentralRowDontMatchEachOther()
    {
        var body = await (await Get(4)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("sentence").GetProperty("hasVideo").GetBoolean().Should().BeFalse();
        body.GetProperty("elsewhere").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task APrivateLineIsNotFound()
    {
        (await Get(3)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignedOutIsRefused()
    {
        (await _client.GetAsync("/api/sentences/1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
