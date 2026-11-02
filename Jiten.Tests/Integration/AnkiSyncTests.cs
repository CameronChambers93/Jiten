using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Sync with Anki by note type: setups, the new-card target, writes per sentence-list format, main fields, flags and folds.</summary>
public class AnkiSyncTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const long OldNote = 1793210088120;
    private const long NewNote = 1793299999999;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedNotes.RemoveRange(userDb.MinedNotes);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        userDb.AnkiNoteTypeSetups.RemoveRange(userDb.AnkiNoteTypeSetups);
        userDb.UserSettings.RemoveRange(userDb.UserSettings);
        await userDb.SaveChangesAsync();

        var jiten = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JitenDbContext>>().CreateDbContext();
        jiten.ExampleSentences.RemoveRange(jiten.ExampleSentences.IgnoreQueryFilters());
        await jiten.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Setup(string model = "Lapis+", string sentenceList = "script", bool updateMain = false, string? knownWords = null,
                                Dictionary<string, string>? roles = null) => new
    {
        modelName = model, filter = (string?)null,
        fieldRoles = roles ?? new Dictionary<string, string>
        {
            ["Expression"] = "word", ["ExpressionReading"] = "wordReading", ["Sentence"] = "sentence", ["SentenceAudio"] = "sentenceAudio",
            ["Sentences"] = "sentenceList"
        },
        preset = "Lapis+", knownWords, importSentences = true, takeFlags = true, foldDuplicates = true, updateMainFields = updateMain, sentenceList,
        scriptVersion = (int?)null
    };

    private Task<HttpResponseMessage> PutSetup(object setup, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/setups").WithUser(user).WithJsonContent(setup));

    private async Task<JsonElement> Get(string url, string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithUser(user));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> Post(string url, object? body = null) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url).WithUser(TestUsers.UserA).WithJsonContent(body ?? new { }));

    private async Task<long> AddLine(string text)
    {
        using var scope = factory.Services.CreateScope();
        await using var jiten = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<JitenDbContext>>().CreateDbContextAsync();
        var line = new ExampleSentence { Text = text, Tokens = [], WordKeys = [], SourceTitle = "オッドタクシー" };
        jiten.ExampleSentences.Add(line);
        await jiten.SaveChangesAsync();
        return line.SentenceId;
    }

    /// <summary>運命 on two Lapis+ notes, each with its own sentence (the old note's first); returns the sentences' ids.</summary>
    private async Task<(long WordId, long OldSentence, long NewSentence)> MineWordOnTwoNotes()
    {
        var first = await AddLine("運命ってあると思いませんか？");
        var second = await AddLine("捨てられる運命ざんす。");
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = new MinedWord { UserId = TestUsers.UserA, Expression = "運命", Reading = "うんめい" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();
        userDb.MinedNotes.AddRange(
            new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = OldNote, ModelName = "Lapis+", DeckName = "Mining" },
            new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = NewNote, ModelName = "Lapis+", DeckName = "Mining" });
        var a = new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = "<b>運命</b>ってあると思いませんか？", Origin = MinedSentenceOrigin.Jiten,
            ExampleSentenceId = first, SourceAnkiNoteId = OldNote, AnkiAudioFilename = $"jiten_{first}.mp3", SortOrder = 0
        };
        var b = new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = "捨てられる<b>運命</b>ざんす。", English = "Doomed to be thrown away.",
            Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = second, SourceAnkiNoteId = NewNote, SortOrder = 1
        };
        userDb.MinedSentences.AddRange(a, b);
        await userDb.SaveChangesAsync();
        return (word.MinedWordId, a.MinedSentenceId, b.MinedSentenceId);
    }

    [Fact]
    public async Task SetupsAreSavedPerUserAndValidated()
    {
        (await PutSetup(Setup())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutSetup(Setup(knownWords: "mature"))).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await Get("/api/anki-sync/setups");
        var setup = list.GetProperty("setups").EnumerateArray().Should().ContainSingle().Subject;
        setup.GetProperty("knownWords").GetString().Should().Be("mature");
        setup.GetProperty("fieldRoles").GetProperty("Sentences").GetString().Should().Be("sentenceList");
        (await Get("/api/anki-sync/setups", TestUsers.UserB)).GetProperty("setups").GetArrayLength().Should().Be(0);

        // A list needs a field to go in; known words need a Word field; roles must be known ones.
        (await PutSetup(Setup(roles: new() { ["Expression"] = "word", ["Sentence"] = "sentence" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutSetup(Setup(sentenceList: "none", knownWords: "seen", roles: new() { ["Sentence"] = "sentence" })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutSetup(Setup(roles: new() { ["Expression"] = "bogus" }))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var delete = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/anki-sync/setups?modelName=Lapis%2B").WithUser(TestUsers.UserA));
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get("/api/anki-sync/setups")).GetProperty("setups").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ChangingKnownWordsResetsWhenTheyWereLastSent()
    {
        (await PutSetup(Setup(knownWords: "seen"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var synced = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/known-words-synced")
            .WithUser(TestUsers.UserA).WithJsonContent(new { syncedAt = "2026-11-02T10:00:00Z", previous = (string?)null }));
        synced.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get("/api/anki-sync/setups")).GetProperty("knownWordsSyncedAt").GetDateTime().Should().Be(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc));

        // Same known words: kept. Now counting mature cards only: every card goes again.
        (await PutSetup(Setup(knownWords: "seen", updateMain: true))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get("/api/anki-sync/setups")).GetProperty("knownWordsSyncedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        var saved = await (await PutSetup(Setup(knownWords: "mature"))).Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("knownWordsSyncedAt").ValueKind.Should().Be(JsonValueKind.Null);

        // A Sync that started before the reset doesn't overwrite it.
        var late = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/known-words-synced")
            .WithUser(TestUsers.UserA).WithJsonContent(new { syncedAt = "2026-11-02T11:00:00Z", previous = "2026-11-02T10:00:00Z" }));
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Get("/api/anki-sync/setups")).GetProperty("knownWordsSyncedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheCardTargetIsKeptOnTheServer()
    {
        (await Get("/api/anki-sync/card-target")).GetProperty("tags").GetString().Should().Be("jiten");
        var put = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/card-target").WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckName = "Mining", modelName = "Lapis+", tags = "jiten anime", padStartMs = 99999, padEndMs = 100 }));
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var target = await Get("/api/anki-sync/card-target");
        target.GetProperty("deckName").GetString().Should().Be("Mining");
        target.GetProperty("padStartMs").GetInt32().Should().Be(5000);
        target.GetProperty("emptyFields").ValueKind.Should().Be(JsonValueKind.Null, "the browser's defaults apply until policies are chosen");
        (await Get("/api/anki-sync/card-target", TestUsers.UserB)).GetProperty("deckName").GetString().Should().Be("");
    }

    [Fact]
    public async Task TheCardTargetKeepsWhatToDoWithEmptyFields()
    {
        var put = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/card-target").WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckName = "Mining", modelName = "Lapis+", tags = "", padStartMs = 0, padEndMs = 0,
                                   emptyFields = new Dictionary<string, string> { ["sentenceEnglish"] = "warn", ["picture"] = "deny", ["source"] = "maybe" } }));
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var policies = (await Get("/api/anki-sync/card-target")).GetProperty("emptyFields");
        policies.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Should().BeEquivalentTo("sentenceEnglish=warn", "picture=deny");
    }

    [Fact]
    public async Task NotesAreWrittenInTheirNoteTypesFormat()
    {
        await MineWordOnTwoNotes();

        // No setup: nothing is written.
        (await Get("/api/mined/sync/notes")).GetProperty("notes").GetArrayLength().Should().Be(0);

        await PutSetup(Setup(sentenceList: "plain"));
        var note = (await Get("/api/mined/sync/notes")).GetProperty("notes").EnumerateArray().Should().ContainSingle().Subject;
        note.GetProperty("ankiNoteId").GetInt64().Should().Be(OldNote);
        note.GetProperty("modelName").GetString().Should().Be("Lapis+");
        note.GetProperty("sentencesHtml").GetString().Should()
            .Be("<ol class=\"jiten-sentence-list\"><li><b>運命</b>ってあると思いませんか？</li><li>捨てられる<b>運命</b>ざんす。<br><small>Doomed to be thrown away.</small></li></ol>");

        await PutSetup(Setup());
        (await Get("/api/mined/sync/notes")).GetProperty("notes")[0].GetProperty("sentencesHtml").GetString().Should()
            .StartWith($"<div class=\"jiten-sentences\" data-note=\"{OldNote}\">");

        // Main fields only, and the main sentence is the kept note's own: nothing to write.
        await PutSetup(Setup(sentenceList: "none", updateMain: true));
        (await Get("/api/mined/sync/notes")).GetProperty("notes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task MainFieldsAreRewrittenWhenTheMainSentenceIsFlagged()
    {
        var (_, oldSentence, newSentence) = await MineWordOnTwoNotes();
        await PutSetup(Setup(sentenceList: "none", updateMain: true));

        // The plain tag flags the sentence on the note's main fields: the kept note's own.
        var flagged = await Post("/api/mined/sync/flags", new { flags = new[] { new { ankiNoteId = OldNote, minedSentenceId = (long?)null } } });
        var applied = (await flagged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied");
        applied.GetArrayLength().Should().Be(1);

        var main = (await Get("/api/mined/sync/notes")).GetProperty("notes").EnumerateArray().Should().ContainSingle().Subject.GetProperty("main");
        main.GetProperty("minedSentenceId").GetInt64().Should().Be(newSentence);
        main.GetProperty("sentence").GetString().Should().Be("捨てられる<b>運命</b>ざんす。");
        main.GetProperty("english").GetString().Should().Be("Doomed to be thrown away.");
        var hash = main.GetProperty("hash").GetString()!;

        // Once written, it isn't written again.
        (await Post("/api/mined/sync/main-written", new { notes = new[] { new { ankiNoteId = OldNote, minedSentenceId = newSentence, hash } } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get("/api/mined/sync/notes")).GetProperty("notes").GetArrayLength().Should().Be(0);

        // Another word's sentence isn't recorded on the note.
        (await Post("/api/mined/sync/main-written", new { notes = new[] { new { ankiNoteId = OldNote, minedSentenceId = oldSentence + 1000, hash } } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get("/api/mined/sync/notes")).GetProperty("notes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task AFoldIsRecordedBeforeTheDeleteAndConfirmedAfter()
    {
        var (wordId, _, _) = await MineWordOnTwoNotes();
        await PutSetup(Setup());

        var folds = await Get("/api/mined/sync/folds");
        folds.GetProperty("candidates").EnumerateArray().Should().ContainSingle().Subject.GetProperty("keep").GetInt64().Should().Be(OldNote);

        (await Post("/api/mined/sync/fold", new { minedWordId = wordId, keepAnkiNoteId = OldNote, foldedAnkiNoteIds = new[] { NewNote } }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        // Another fold waits until this one is finished or dropped.
        (await Post("/api/mined/sync/fold", new { minedWordId = wordId, keepAnkiNoteId = NewNote, foldedAnkiNoteIds = new[] { OldNote } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        folds = await Get("/api/mined/sync/folds");
        folds.GetProperty("candidates").GetArrayLength().Should().Be(0);
        var pending = folds.GetProperty("pending").EnumerateArray().Should().ContainSingle().Subject;
        pending.GetProperty("keep").GetInt64().Should().Be(OldNote);
        pending.GetProperty("folded")[0].GetInt64().Should().Be(NewNote);

        // Dropped (the delete didn't happen): a candidate again. Then recorded and confirmed.
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/mined/sync/fold/{wordId}").WithUser(TestUsers.UserA)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Get("/api/mined/sync/folds")).GetProperty("candidates").GetArrayLength().Should().Be(1);
        await Post("/api/mined/sync/fold", new { minedWordId = wordId, keepAnkiNoteId = OldNote, foldedAnkiNoteIds = new[] { NewNote } });

        var confirmed = await Post($"/api/mined/sync/fold/{wordId}/confirm");
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        var write = (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("write");
        write.GetProperty("ankiNoteId").GetInt64().Should().Be(OldNote);
        write.GetProperty("sentencesHtml").GetString().Should().Contain("ざんす");

        folds = await Get("/api/mined/sync/folds");
        folds.GetProperty("candidates").GetArrayLength().Should().Be(0);
        folds.GetProperty("pending").GetArrayLength().Should().Be(0);
        (await Post($"/api/mined/sync/fold/{wordId}/confirm")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NoteTypesThatDontFoldArentCandidates()
    {
        await MineWordOnTwoNotes();
        await PutSetup(new
        {
            modelName = "Lapis+", filter = (string?)null, fieldRoles = new Dictionary<string, string> { ["Expression"] = "word" }, preset = (string?)null,
            knownWords = (string?)null, importSentences = false, takeFlags = false, foldDuplicates = false, updateMainFields = false, sentenceList = "none",
            scriptVersion = (int?)null
        });
        (await Get("/api/mined/sync/folds")).GetProperty("candidates").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task APartlyDeletedFoldKeepsTheNotesStillInAnki()
    {
        var (wordId, _, _) = await MineWordOnTwoNotes();
        await PutSetup(Setup());
        await Post("/api/mined/sync/fold", new { minedWordId = wordId, keepAnkiNoteId = OldNote, foldedAnkiNoteIds = new[] { NewNote } });

        // None of the folded notes is gone: they stay the word's, and the word is a candidate again.
        (await Post($"/api/mined/sync/fold/{wordId}/confirm", new { goneAnkiNoteIds = Array.Empty<long>() })).StatusCode.Should().Be(HttpStatusCode.OK);
        var folds = await Get("/api/mined/sync/folds");
        folds.GetProperty("pending").GetArrayLength().Should().Be(0);
        folds.GetProperty("candidates").EnumerateArray().Should().ContainSingle().Subject.GetProperty("notes").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task TheKeptNoteIsTheOldestOfASyncedNoteType()
    {
        await MineWordOnTwoNotes();
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.MinedNotes.Where(n => n.AnkiNoteId == OldNote).ExecuteUpdateAsync(u => u.SetProperty(n => n.ModelName, "Old note type"));
        }

        await PutSetup(Setup());
        (await Get("/api/mined/sync/notes")).GetProperty("notes").EnumerateArray().Should().ContainSingle().Subject
            .GetProperty("ankiNoteId").GetInt64().Should().Be(NewNote);
    }
}
