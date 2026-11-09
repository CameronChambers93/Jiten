using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.Mining;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Parser.SpeechBoundaries;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Submitted fixes at parse time: which corrections a reparse applies, which titles are waiting for one, and a fix that
/// finds no line going back on its submitter's list. The parse itself needs the parser runtime, so it's checked against
/// Postgres, not here.
/// </summary>
public class ParseCorrectionTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);
    private static readonly DateTime Parsed = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        await userDb.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.SentenceCorrections.ExecuteDeleteAsync();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        db.Decks.AddRange(new Deck { DeckId = 1, OriginalTitle = "Osomatsu-san", RomajiTitle = "Osomatsu-san", MediaType = MediaType.Anime, LastUpdate = Parsed },
                          new Deck { DeckId = 2, OriginalTitle = "Episode 14", MediaType = MediaType.Anime, ParentDeckId = 1, LastUpdate = Parsed },
                          new Deck { DeckId = 3, OriginalTitle = "Episode 3", MediaType = MediaType.Anime, ParentDeckId = 1, LastUpdate = Parsed },
                          new Deck { DeckId = 4, OriginalTitle = "Odd Taxi", MediaType = MediaType.Anime, LastUpdate = Parsed });
        db.ExampleSentences.AddRange(
            new ExampleSentence { SentenceId = 1, DeckId = 2, Position = 40, Text = "うん。", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 2, DeckId = 3, Position = 5, Text = "行こう。", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 3, DeckId = 4, Position = 9, Text = "そうか。", Tokens = NoWords, WordKeys = [] });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Correct(long sentenceId, int deckId, string source, string? text, DateTime at, string? english = null, int position = 0)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.SentenceCorrections.Add(new SentenceCorrection
        {
            ExampleSentenceId = sentenceId, DeckId = deckId, Position = position, SourceText = source, Text = text, English = english,
            UserId = TestUsers.Admin, CreatedAt = at
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AReparseAppliesEachLinesLatestTextFixAtItsPositionNow()
    {
        await Correct(1, 2, "はい。", "ええ。", Parsed.AddDays(1), position: 3);
        await Correct(1, 2, "はい。", "うん。", Parsed.AddDays(2), position: 3);
        await Correct(1, 2, "はい。", null, Parsed.AddDays(3), english: "Yeah.");
        await Correct(3, 4, "そっか。", "そうか。", Parsed.AddDays(1));

        using var scope = factory.Services.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<ParseCorrectionService>().LoadAsync([2, 3]);

        loaded.Keys.Should().Equal(2);
        loaded[2].Select(c => (c.Correction, c.UserId)).Should().Equal((new LineCorrection(1, "はい。", "うん。", 40), TestUsers.Admin));
    }

    [Fact]
    public async Task TitlesWaitForAReparseUntilTheirEpisodesAreParsedAgain()
    {
        await Correct(1, 2, "はい。", "うん。", Parsed.AddDays(1));
        await Correct(2, 3, "行くぞ。", "行こう。", Parsed.AddDays(1));
        await Correct(3, 4, "そっか。", "そうか。", Parsed.AddDays(-1));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/sentence-flags/corrected-titles").WithAdmin();
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var titles = await response.Content.ReadFromJsonAsync<JsonElement>();

        titles.EnumerateArray().Select(t => (t.GetProperty("deckId").GetInt32(), t.GetProperty("corrections").GetInt32()))
              .Should().Equal((1, 2));

        var queued = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/sentence-flags/reparse-corrected").WithAdmin());
        (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task OnlyAdminsSeeOrReparseCorrectedTitles()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/sentence-flags/corrected-titles").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/sentence-flags/reparse-corrected").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AFixThatFindsNoLineGoesBackOnItsSubmittersListOnce()
    {
        var gone = new LineCorrection(1, "はい。", "うん。", 40);
        var users = new Dictionary<long, string> { [1] = TestUsers.Admin };

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ParseCorrectionService>().ReportUnmatchedAsync(2, [gone], users);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ParseCorrectionService>().ReportUnmatchedAsync(2, [gone], users);

        using var check = factory.Services.CreateScope();
        var flags = await check.ServiceProvider.GetRequiredService<UserDbContext>().SentenceFlags.AsNoTracking().ToListAsync();
        flags.Should().ContainSingle();
        flags[0].Should().BeEquivalentTo(new { UserId = TestUsers.Admin, ExampleSentenceId = 1L, Reason = SentenceFlagReason.Japanese, ResolvedAt = (DateTime?)null },
                                         o => o.ExcludingMissingMembers());
        flags[0].Note.Should().Contain("うん。");
    }

    [Fact]
    public async Task ALineAlreadyOutOfTheDeckIsntCorrectedOrReportedAgain()
    {
        await Correct(2, 3, "行くぞ。", "行こう。", Parsed.AddDays(1));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.ExampleSentences.IgnoreQueryFilters().Where(s => s.SentenceId == 2).ExecuteUpdateAsync(u => u.SetProperty(s => s.RemovedFromDeck, true));
        }

        using var load = factory.Services.CreateScope();
        (await load.ServiceProvider.GetRequiredService<ParseCorrectionService>().LoadAsync([3])).Should().BeEmpty();
    }
}
