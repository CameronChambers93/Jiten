using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Flagging sentences: the to-do list, flags from cards, and hiding a flagged line from the user's card rotation.</summary>
public class SentenceFlagTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const long Note = 1793210088120;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        // The factory's reset doesn't know the sentence store.
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedNotes.RemoveRange(userDb.MinedNotes);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        userDb.AnkiNoteTypeSetups.RemoveRange(userDb.AnkiNoteTypeSetups);
        await userDb.SaveChangesAsync();
        // Sync writes the rotation script's list to Lapis+ notes.
        userDb.AnkiNoteTypeSetups.Add(new AnkiNoteTypeSetup
        {
            UserId = TestUsers.UserA, ModelName = "Lapis+", FieldRolesJson = """{"Expression":"word","Sentence":"sentence","Sentences":"sentenceList"}""",
            ImportSentences = true, TakeFlags = true, FoldDuplicates = true, SentenceList = AnkiSentenceListFormat.Script
        });
        await userDb.SaveChangesAsync();

        var jiten = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JitenDbContext>>().CreateDbContext();
        jiten.ExampleSentences.RemoveRange(jiten.ExampleSentences.IgnoreQueryFilters());
        await jiten.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> AddLine(string text, string? ownerId = null)
    {
        using var scope = factory.Services.CreateScope();
        await using var jiten = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<JitenDbContext>>().CreateDbContextAsync();
        var line = new ExampleSentence { Text = text, Tokens = [], WordKeys = [], OwnerId = ownerId, SourceTitle = "オッドタクシー" };
        jiten.ExampleSentences.Add(line);
        await jiten.SaveChangesAsync();
        return line.SentenceId;
    }

    /// <summary>A word on one Anki note with these lines, in order; returns the mined sentences' ids.</summary>
    private async Task<List<long>> MineWord(params long[] lines)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = new MinedWord { UserId = TestUsers.UserA, Expression = "運命", Reading = "うんめい" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();

        userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = Note, ModelName = "Lapis+" });
        var sentences = lines.Select((line, i) => new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = $"line {i}", Origin = MinedSentenceOrigin.Jiten,
            ExampleSentenceId = line, SortOrder = i
        }).ToList();
        userDb.MinedSentences.AddRange(sentences);
        await userDb.SaveChangesAsync();
        return sentences.Select(s => s.MinedSentenceId).ToList();
    }

    private Task<HttpResponseMessage> Flag(long sentenceId, string? reason = "english", string? note = null, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/sentence-flags").WithUser(user).WithJsonContent(new { sentenceId, reason, note }));

    private Task<HttpResponseMessage> CardFlag(long minedSentenceId) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/sync/flags")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { flags = new[] { new { ankiNoteId = Note, minedSentenceId } } }));

    private async Task<JsonElement[]> Flags(string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/sentence-flags").WithUser(user));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("flags").EnumerateArray().ToArray();
    }

    private async Task<string> CardField()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mined/sync/notes").WithUser(TestUsers.UserA));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("notes")[0].GetProperty("sentencesHtml").GetString()!;
    }

    [Fact]
    public async Task AnyLineCanBeFlaggedAndUpdated()
    {
        var line = await AddLine("運命を感じた");

        (await Flag(line, "english")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Flag(line, "media", "  音声がずれている ")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var flag = (await Flags()).Should().ContainSingle().Subject;
        flag.GetProperty("reason").GetString().Should().Be("media");
        flag.GetProperty("note").GetString().Should().Be("音声がずれている");
        flag.GetProperty("text").GetString().Should().Be("運命を感じた");
        flag.GetProperty("source").GetString().Should().Be("オッドタクシー");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad")]
    public async Task AFlagFromJitenNeedsAReason(string? reason)
    {
        (await Flag(await AddLine("運命"), reason)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnotherUsersPrivateLineCantBeFlagged()
    {
        var line = await AddLine("秘密", TestUsers.UserB);

        (await Flag(line)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Flag(line, user: TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ACardFlagArrivesNeedingAReasonOnce()
    {
        var mined = await MineWord(await AddLine("運命"));

        (await CardFlag(mined[0])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CardFlag(mined[0])).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Flags()).Should().ContainSingle().Which.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AFlaggedLineLeavesRotationForItsUserUntilDismissed()
    {
        var first = await AddLine("一つ目");
        var second = await AddLine("二つ目");
        var mined = await MineWord(first, second);

        await Flag(second, user: TestUsers.UserB);
        (await CardField()).Should().Contain($"data-id=\"{mined[1]}\"");

        await Flag(second);
        (await CardField()).Should().NotContain($"data-id=\"{mined[1]}\"");

        var flag = (await Flags()).Single().GetProperty("sentenceFlagId").GetInt64();
        var dismissed = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/sentence-flags/{flag}/dismiss").WithUser(TestUsers.UserA));
        dismissed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CardField()).Should().Contain($"data-id=\"{mined[1]}\"");
        (await Flags()).Should().BeEmpty();
    }

    [Fact]
    public async Task ANoteWithEverySentenceFlaggedKeepsItsMainOne()
    {
        var first = await AddLine("一つ目");
        var second = await AddLine("二つ目");
        var mined = await MineWord(first, second);

        await Flag(first);
        await Flag(second);

        var field = await CardField();
        field.Should().Contain($"data-id=\"{mined[0]}\"");
        field.Should().NotContain($"data-id=\"{mined[1]}\"");
    }

    [Fact]
    public async Task OnlyTheFlagsOwnerCanDismissIt()
    {
        await Flag(await AddLine("運命"));
        var flag = (await Flags()).Single().GetProperty("sentenceFlagId").GetInt64();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/sentence-flags/{flag}/dismiss").WithUser(TestUsers.UserB));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Flags()).Should().ContainSingle();
    }

    private async Task<JsonElement> AdminFlags(string? reason = null)
    {
        var url = reason == null ? "/api/admin/sentence-flags" : $"/api/admin/sentence-flags?reason={reason}";
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private Task<HttpResponseMessage> MarkFixed(long sentenceId) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/sentence-flags/{sentenceId}/fixed").WithAdmin());

    [Fact]
    public async Task AdminsSeeEachFlaggedPublicLineOnceMostFlaggedFirst()
    {
        var once = await AddLine("一回");
        var twice = await AddLine("二回");
        await Flag(once, "media");
        await Flag(twice, "english", "訳が違う");
        await Flag(twice, "japanese", user: TestUsers.UserB);

        var page = await AdminFlags();
        page.GetProperty("totalCount").GetInt32().Should().Be(2);
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("sentenceId").GetInt64()).Should().Equal(twice, once);
        items[0].GetProperty("flagCount").GetInt32().Should().Be(2);
        items[0].GetProperty("reasons").GetProperty("english").GetInt32().Should().Be(1);
        items[0].GetProperty("notes").EnumerateArray().Single().GetProperty("note").GetString().Should().Be("訳が違う");
        items[0].GetProperty("notes")[0].TryGetProperty("userId", out _).Should().BeFalse();

        (await AdminFlags("media")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sentenceId").GetInt64()).Should().Equal(once);
    }

    [Fact]
    public async Task AdminsDontSeePrivateLines()
    {
        await Flag(await AddLine("秘密", TestUsers.UserA));

        (await AdminFlags()).GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task MarkingALineFixedBringsItBackForEveryoneWhoFlaggedIt()
    {
        var first = await AddLine("一つ目");
        var second = await AddLine("二つ目");
        var mined = await MineWord(first, second);
        await Flag(second);
        await Flag(second, user: TestUsers.UserB);

        var response = await MarkFixed(second);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("closed").GetInt32().Should().Be(2);

        (await Flags()).Should().BeEmpty();
        (await Flags(TestUsers.UserB)).Should().BeEmpty();
        (await CardField()).Should().Contain($"data-id=\"{mined[1]}\"");
        (await MarkFixed(second)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.SentenceFlags.Select(f => f.Resolution).ToListAsync()).Should().AllBe(SentenceFlagResolution.Fixed);
    }

    [Fact]
    public async Task OnlyAdminsCanMarkALineFixed()
    {
        var line = await AddLine("運命");
        await Flag(line);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/admin/sentence-flags/{line}/fixed").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Flags()).Should().ContainSingle();
    }
}
