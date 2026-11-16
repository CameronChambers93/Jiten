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

/// <summary>Bulk sentence checks: leasing batches, posting verdicts, and the admin queue's accept and dismiss.</summary>
public class SentenceCheckTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);
    private const string Line = "えっ、なに、その頭！";
    private const string AiEnglish = "Huh? What's on your head?";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.SentenceCorrections.ExecuteDeleteAsync();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Sentences.ExecuteDeleteAsync();
        db.Decks.AddRange(new Deck { DeckId = 1, OriginalTitle = "Osomatsu-san", MediaType = MediaType.Anime },
                          new Deck { DeckId = 2, OriginalTitle = "Episode 14", MediaType = MediaType.Anime, ParentDeckId = 1 },
                          new Deck { DeckId = 3, OriginalTitle = "Episode 3", MediaType = MediaType.Anime, ParentDeckId = 1 },
                          new Deck { DeckId = 4, OriginalTitle = "A book", MediaType = MediaType.Novel });
        db.Sentences.AddRange(new Sentence { Id = 10, TextHash = [1], Text = Line }, new Sentence { Id = 11, TextHash = [2], Text = "本の行" });
        db.ExampleSentenceTranslations.Add(new ExampleSentenceTranslation
            { CentralSentenceId = 10, Text = AiEnglish, Provider = "deepl", Model = "m", CreatedAt = DateTime.UtcNow.AddDays(-1) });
        db.ExampleSentences.AddRange(
            // Subtitle English from deck 2's tracks
            new ExampleSentence { SentenceId = 1, DeckId = 2, Position = 1, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            // AI English, twice with the same text
            new ExampleSentence { SentenceId = 2, DeckId = 3, Position = 1, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 5, DeckId = 3, Position = 2, Text = "いや～ん", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 6, DeckId = 3, Position = 3, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            // No English at all
            new ExampleSentence { SentenceId = 3, DeckId = 4, Position = 1, Text = "本の行", Tokens = NoWords, WordKeys = [], CentralSentenceId = 11 },
            // Private
            new ExampleSentence { SentenceId = 4, Text = Line, Tokens = NoWords, WordKeys = [], OwnerId = TestUsers.UserB, CentralSentenceId = 10 });
        db.DeckSubtitleTracks.Add(new DeckSubtitleTrack
        {
            DeckId = 2,
            CuesJson = """[{"s":1000,"e":2000,"t":"トト子ちゃ～ん！"},{"s":2500,"e":4000,"t":"えっ、なに、その頭！"},{"s":4500,"e":6000,"t":"いや～ん"}]"""
        });
        db.DeckEnglishTracks.Add(new DeckEnglishTrack
        {
            DeckId = 2, Source = "test", SavedAt = DateTime.UtcNow.AddDays(-1),
            CuesJson = """[{"s":1000,"e":2000,"t":"Totoko!"},{"s":2500,"e":4000,"t":"Huh? What's with your hair?"},{"s":4500,"e":6000,"t":"Ugh."}]"""
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string user = TestUsers.Admin)
    {
        var request = new HttpRequestMessage(method, url);
        if (user == TestUsers.Admin) request.WithAdmin();
        else request.WithUser(user);
        if (body != null) request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private async Task<JsonElement> Json(HttpMethod method, string url, object? body = null)
    {
        var response = await Send(method, url, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> Next(string query = "") => Json(HttpMethod.Get, $"/api/admin/sentence-checks/next?size=50{query}");

    private static List<long> Ids(JsonElement item) => item.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ToList();

    private Task<JsonElement> Post(params object[] results) =>
        Json(HttpMethod.Post, "/api/admin/sentence-checks", new { checker = "test", results });

    private static object Finding(string kind, string? english = null, string? text = null) =>
        new { kind, note = "wrong", suggestedEnglish = english, suggestedText = text };

    private async Task<T> InDb<T>(Func<JitenDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    [Fact]
    public async Task NextLeasesDeckLinesWithEnglish_GroupedByTextAndEnglish()
    {
        var batch = await Next();

        var items = batch.GetProperty("items").EnumerateArray().ToList();
        items.Select(Ids).Should().BeEquivalentTo(new[] { new List<long> { 1 }, new List<long> { 2, 6 } });
        var subtitle = items.Single(i => Ids(i).Contains(1));
        subtitle.GetProperty("english").GetString().Should().Be("Huh? What's with your hair?");
        subtitle.GetProperty("origin").GetString().Should().Be("subtitles");
        subtitle.GetProperty("title").GetString().Should().Be("Osomatsu-san");
        subtitle.GetProperty("episode").GetString().Should().Be("Episode 14");
        var ai = items.Single(i => Ids(i).Contains(2));
        ai.GetProperty("origin").GetString().Should().Be("ai");
        ai.GetProperty("after").EnumerateArray().First().GetProperty("text").GetString().Should().Be("いや～ん");
        batch.GetProperty("exhausted").GetBoolean().Should().BeTrue();

        (await Next()).GetProperty("lines").GetInt32().Should().Be(0, "leased lines aren't handed out twice");
    }

    [Fact]
    public async Task NextTakesOnlyThePickedTitles()
    {
        (await Next("&deckIds=4")).GetProperty("lines").GetInt32().Should().Be(0);
        (await Next("&deckIds=1")).GetProperty("lines").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task VerdictsPassOrFlag_AndAPassedLineIsDoneUntilItsTextChanges()
    {
        await Next();
        var summary = await Post(new { ids = new[] { 1L }, findings = Array.Empty<object>() },
                                 new { ids = new[] { 2L, 6L }, findings = new[] { Finding("english-quality", "Huh? What's with your head?") } },
                                 new { ids = new[] { 3L }, findings = Array.Empty<object>() });
        summary.GetProperty("passed").GetInt32().Should().Be(1);
        summary.GetProperty("flagged").GetInt32().Should().Be(2);
        summary.GetProperty("rejected").GetInt32().Should().Be(1, "line 3 was never leased");

        (await Next()).GetProperty("lines").GetInt32().Should().Be(0);

        await InDb(db => db.ExampleSentences.Where(s => s.SentenceId == 1).ExecuteUpdateAsync(u => u.SetProperty(s => s.Text, "えっ、なに、その髪！")));
        (await Next()).GetProperty("items").EnumerateArray().Select(Ids).Should().BeEquivalentTo(new[] { new List<long> { 1 } });
    }

    [Fact]
    public async Task AnUnknownKindIsRefused()
    {
        await Next();
        var response = await Send(HttpMethod.Post, "/api/admin/sentence-checks",
                                  new { checker = "test", results = new[] { new { ids = new[] { 1L }, findings = new[] { Finding("style") } } } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AcceptingAiEnglishReplacesTheCachedTranslationForEveryLineWithTheText()
    {
        await Next();
        await Post(new { ids = new[] { 2L, 6L }, findings = new[] { Finding("english-quality", "Huh? What's with your head?") } });

        var queue = await Json(HttpMethod.Get, "/api/admin/sentence-checks?kind=english-quality");
        queue.GetProperty("total").GetInt32().Should().Be(2);

        (await Send(HttpMethod.Post, "/api/admin/sentence-checks/2/accept", new { english = "Huh? What's with your head?" })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var cached = await InDb(db => db.ExampleSentenceTranslations.SingleAsync(t => t.CentralSentenceId == 10));
        (cached.Text, cached.Provider).Should().Be(("Huh? What's with your head?", "reviewed"));
        (await InDb(db => db.SentenceChecks.Where(c => c.ExampleSentenceId != 1).Select(c => c.Status).ToListAsync()))
            .Should().Equal(SentenceCheckStatus.Accepted, SentenceCheckStatus.Accepted);
        (await Next()).GetProperty("lines").GetInt32().Should().Be(0, "2 and 6 are done with the new English; 1 is still leased");
    }

    [Fact]
    public async Task AcceptingSubtitleEnglishMakesItTheSharedFix()
    {
        await Next();
        await Post(new { ids = new[] { 1L }, findings = new[] { Finding("english-fit", "Huh? What's with your head?") } });

        (await Send(HttpMethod.Post, "/api/admin/sentence-checks/1/accept", new { english = "What's with your head?" })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await InDb(db => db.ExampleSentences.Where(s => s.SentenceId == 1).Select(s => s.English).SingleAsync())).Should().Be("What's with your head?");
        (await InDb(db => db.SentenceCorrections.CountAsync(c => c.ExampleSentenceId == 1 && c.English == "What's with your head?"))).Should().Be(1);
        (await InDb(db => db.ExampleSentenceTranslations.SingleAsync(t => t.CentralSentenceId == 10))).Text.Should().Be(AiEnglish);
    }

    [Fact]
    public async Task AcceptingJapaneseIsASharedTextFixWithItsCorrection()
    {
        await Next();
        await Post(new { ids = new[] { 1L }, findings = new[] { Finding("japanese", text: "えっ、なに、その髪！") } });

        (await Send(HttpMethod.Post, "/api/admin/sentence-checks/1/accept", new { text = "えっ、なに、その髪！" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var line = await InDb(db => db.ExampleSentences.SingleAsync(s => s.SentenceId == 1));
        (line.Text, line.SourceText).Should().Be(("えっ、なに、その髪！", Line));
        (await InDb(db => db.SentenceCorrections.CountAsync(c => c.ExampleSentenceId == 1 && c.Text == "えっ、なに、その髪！"))).Should().Be(1);
        (await InDb(db => db.SentenceChecks.SingleAsync(c => c.ExampleSentenceId == 1))).CheckedText.Should().Be("えっ、なに、その髪！");
    }

    [Fact]
    public async Task DismissingClosesTheFlagWithANote()
    {
        await Next();
        await Post(new { ids = new[] { 1L }, findings = new[] { Finding("split") } });

        (await Send(HttpMethod.Post, "/api/admin/sentence-checks/1/dismiss", new { note = "fine as one line" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var check = await InDb(db => db.SentenceChecks.SingleAsync(c => c.ExampleSentenceId == 1));
        (check.Status, check.ResolutionNote).Should().Be((SentenceCheckStatus.Dismissed, "fine as one line"));
        (await Send(HttpMethod.Post, "/api/admin/sentence-checks/1/dismiss", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProgressCountsPerTitle()
    {
        await Next();
        await Post(new { ids = new[] { 1L }, findings = Array.Empty<object>() });

        var progress = (await Json(HttpMethod.Get, "/api/admin/sentence-checks/progress?deckIds=1")).EnumerateArray().Single();
        progress.GetProperty("lines").GetInt32().Should().Be(4);
        progress.GetProperty("passed").GetInt32().Should().Be(1);
        progress.GetProperty("leased").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task OnlyAdminsCanUseChecks()
    {
        (await Send(HttpMethod.Get, "/api/admin/sentence-checks/next", user: TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
