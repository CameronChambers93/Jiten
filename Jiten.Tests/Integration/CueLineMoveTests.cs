using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>After a title is ingested again as whole-cue lines: mined sentences and flags move to their new lines and are rebuilt.</summary>
public class CueLineMoveTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);

    private readonly HttpClient _client = factory.CreateClient();
    private long _moved, _stays;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        db.Decks.Add(new Deck { DeckId = 1, OriginalTitle = "Ken", MediaType = MediaType.YouTube, SentenceCount = 3 });
        db.DeckSubtitleTracks.Add(new DeckSubtitleTrack { DeckId = 1, CuesJson = "[]" });
        db.ExampleSentences.AddRange(
            // What the re-ingest left: the old sentence, kept because it was mined, and the new whole-cue lines
            new ExampleSentence { SentenceId = 1, DeckId = 1, Position = 2, Text = "今、僕は台湾のアパートにいます。", Tokens = NoWords, WordKeys = [], RemovedFromDeck = true },
            new ExampleSentence { SentenceId = 2, DeckId = 1, Position = 7, Text = "消えた行。", Tokens = NoWords, WordKeys = [], RemovedFromDeck = true },
            new ExampleSentence { SentenceId = 10, DeckId = 1, Position = 0, Text = "皆さん、こんばんは！", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 11, DeckId = 1, Position = 1, Text = "今、夜です。今、僕は台湾のアパートにいます。", Tokens = NoWords, WordKeys = [] });
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        await userDb.SaveChangesAsync();
        var word = new MinedWord { UserId = TestUsers.UserA, Expression = "アパート", Reading = "アパート" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();
        var moved = new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = 1, TargetPosition = 8, TargetLength = 4,
            English = "I'm in my apartment in Taiwan.", Furigana = "old furigana"
        };
        var stays = new MinedSentence { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = 2 };
        userDb.MinedSentences.AddRange(moved, stays);
        userDb.SentenceFlags.Add(new SentenceFlag { UserId = TestUsers.UserA, ExampleSentenceId = 1, Reason = "english" });
        await userDb.SaveChangesAsync();
        (_moved, _stays) = (moved.MinedSentenceId, stays.MinedSentenceId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<MinedLineMover.Report> Move(bool dryRun = false)
    {
        using var scope = factory.Services.CreateScope();
        return await MinedLineMover.MoveAsync(scope.ServiceProvider.GetRequiredService<JitenDbContext>(),
                                              scope.ServiceProvider.GetRequiredService<UserDbContext>(), dryRun);
    }

    [Fact]
    public async Task AMinedSentenceMovesToTheLineHoldingItsTextWithItsWordShifted()
    {
        var report = await Move();

        report.SentencesMoved.Should().Be(1);
        report.FlagsMoved.Should().Be(1);
        report.Unplaced.Should().ContainSingle(u => u.Id == _stays && u.Text == "消えた行。");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var moved = await userDb.MinedSentences.AsNoTracking().SingleAsync(s => s.MinedSentenceId == _moved);
        moved.ExampleSentenceId.Should().Be(11);
        moved.TargetPosition.Should().Be(14);
        moved.NeedsRebuild.Should().BeTrue();
        (await userDb.MinedSentences.AsNoTracking().SingleAsync(s => s.MinedSentenceId == _stays)).ExampleSentenceId.Should().Be(2);
        (await userDb.SentenceFlags.AsNoTracking().SingleAsync()).ExampleSentenceId.Should().Be(11);
    }

    [Fact]
    public async Task ADryRunChangesNothing()
    {
        (await Move(dryRun: true)).SentencesMoved.Should().Be(1);

        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<UserDbContext>().MinedSentences.AsNoTracking().SingleAsync(s => s.MinedSentenceId == _moved))
            .ExampleSentenceId.Should().Be(1);
    }

    [Fact]
    public async Task MovedSentencesAreListedWithTheirLineAndRebuildingKeepsEnglishThatCameBackEmpty()
    {
        await Move();

        var list = await (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mined/rebuild").WithUser(TestUsers.UserA)))
                         .Content.ReadFromJsonAsync<JsonElement>();
        var item = list.EnumerateArray().Single();
        item.GetProperty("minedSentenceId").GetInt64().Should().Be(_moved);
        var sentence = item.GetProperty("sentence");
        sentence.GetProperty("text").GetString().Should().Be("今、夜です。今、僕は台湾のアパートにいます。");
        sentence.GetProperty("wordPosition").GetInt32().Should().Be(14);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/mined/sentences/{_moved}/rebuild").WithUser(TestUsers.UserA)
                                                   .WithJsonContent(new { sentence = "今、夜です。今、僕は台湾の<b>アパート</b>にいます。", furigana = (string?)null, english = (string?)null }));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<UserDbContext>().MinedSentences.AsNoTracking().SingleAsync(s => s.MinedSentenceId == _moved);
        stored.Text.Should().Be("今、夜です。今、僕は台湾の<b>アパート</b>にいます。");
        stored.English.Should().Be("I'm in my apartment in Taiwan.");
        stored.Furigana.Should().BeNull();
        stored.NeedsRebuild.Should().BeFalse();

        var other = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/mined/sentences/{_moved}/rebuild").WithUser(TestUsers.UserB)
                                                .WithJsonContent(new { sentence = "x" }));
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ATextOnTwoNewLinesIsNotGuessed()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.ExampleSentences.Add(new ExampleSentence { SentenceId = 12, DeckId = 1, Position = 2, Text = "今、僕は台湾のアパートにいます。ほんとに。", Tokens = NoWords, WordKeys = [] });
            await db.SaveChangesAsync();
        }

        var report = await Move();

        report.SentencesMoved.Should().Be(0);
        report.Ambiguous.Should().Be(2);
        report.Unplaced.Should().Contain(u => u.Id == _moved);
    }
}
