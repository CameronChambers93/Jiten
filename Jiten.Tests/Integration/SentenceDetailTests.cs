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
            new ExampleSentence { SentenceId = 5, DeckId = 3, Text = "また別の行", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 6, DeckId = 1, Position = 5, Text = "そん時は驚いたよ。", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 7, DeckId = 1, Position = 6, Text = "ほんとに近道だったし。", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 8, DeckId = 1, Position = 8, Text = "白川さんも来るかい？", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 9, DeckId = 3, Text = "去年の3月に終わっちゃった。", Tokens = NoWords, WordKeys = [] });
        db.DeckSubtitleTracks.Add(new DeckSubtitleTrack
        {
            DeckId = 3,
            CuesJson = """[{"s":0,"e":1000,"t":"それで\n去年の3月に"},{"s":1100,"e":2000,"t":"終わっちゃった\nえっ？"}]"""
        });
        db.DeckSubtitleTracks.Add(new DeckSubtitleTrack
        {
            DeckId = 1,
            CuesJson = """[{"s":1000,"e":2000,"t":"あの道を通ったんだよ"},{"s":2500,"e":5000,"t":"そん時は驚いたよ\nほんとに近道だったし"},{"s":6000,"e":7000,"t":"白川さんも来るかい？"},{"s":8000,"e":9000,"t":"いいや\n（今井）同じ行"}]"""
        });
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
    public async Task TheClipShowsTheOtherSpeechInTheSentencesCue()
    {
        var body = await (await Get(6)).Content.ReadFromJsonAsync<JsonElement>();

        var clip = body.GetProperty("clip").EnumerateArray()
                       .Select(l => (l.GetProperty("text").GetString(), l.GetProperty("isSentence").GetBoolean(),
                                     l.GetProperty("sentenceId").ValueKind == JsonValueKind.Null ? (long?)null : l.GetProperty("sentenceId").GetInt64()))
                       .ToList();
        clip.Should().Equal(("そん時は驚いたよ。", true, null), ("ほんとに近道だったし", false, 7L));
        body.GetProperty("before").EnumerateArray().Select(l => l.GetProperty("text").GetString()).Should().Equal("あの道を通ったんだよ");
        body.GetProperty("after").EnumerateArray().First().GetProperty("text").GetString().Should().Be("白川さんも来るかい？");
    }

    [Fact]
    public async Task AnOtherLineThatIsNoExampleSentenceHasNoLink()
    {
        var body = await (await Get(1)).Content.ReadFromJsonAsync<JsonElement>();

        var clip = body.GetProperty("clip").EnumerateArray().ToList();
        clip.Select(l => l.GetProperty("text").GetString()).Should().Equal("いいや", "同じ行");
        clip[0].GetProperty("sentenceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ASentenceSpanningCuesShowsTheOtherLinesOfBoth()
    {
        var body = await (await Get(9)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("clip").EnumerateArray()
            .Select(l => (l.GetProperty("startMs").GetInt32(), l.GetProperty("text").GetString(), l.GetProperty("isSentence").GetBoolean()))
            .Should().Equal((0, "それで", false), (0, "去年の3月に終わっちゃった。", true), (1100, "えっ？", false));
    }

    [Fact]
    public async Task ASentenceAloneInItsCueHasNoClip()
    {
        var body = await (await Get(8)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("clip").GetArrayLength().Should().Be(0);
        body.GetProperty("before").GetArrayLength().Should().Be(2);
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
