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

    private Task<HttpResponseMessage> PutTargets(object targets, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/card-target").WithUser(user).WithJsonContent(targets));

    [Fact]
    public async Task TheCardTargetsAreKeptOnTheServer()
    {
        var empty = await Get("/api/anki-sync/card-target");
        empty.GetProperty("targets").EnumerateArray().Should().ContainSingle().Which.GetProperty("tags").GetString().Should().Be("jiten");

        var put = await PutTargets(new
        {
            targets = new object[]
            {
                new { deckName = "Anime Mining", modelName = "Lapis+", tags = "jiten anime",
                      when = new { mediaTypes = new[] { 1, 2, 99 }, titles = new[] { new { deckId = 7, title = "ゆるキャン△" }, new { deckId = 7, title = "again" } } } },
                new { deckName = "Mining", modelName = "Lapis+", tags = "jiten", when = new { mediaTypes = new[] { 4 }, titles = Array.Empty<object>() } },
            },
            padStartMs = 99999, padEndMs = 100
        });
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var saved = await Get("/api/anki-sync/card-target");
        saved.GetProperty("padStartMs").GetInt32().Should().Be(5000);
        var targets = saved.GetProperty("targets").EnumerateArray().ToList();
        targets.Should().HaveCount(2);
        var first = targets[0].GetProperty("when");
        first.GetProperty("mediaTypes").EnumerateArray().Select(m => m.GetInt32()).Should().Equal(1, 2);
        first.GetProperty("titles").EnumerateArray().Should().ContainSingle().Which.GetProperty("title").GetString().Should().Be("ゆるキャン△");
        targets[1].GetProperty("when").ValueKind.Should().Be(JsonValueKind.Null, "the last target takes everything else");
        (await Get("/api/anki-sync/card-target", TestUsers.UserB)).GetProperty("targets")[0].GetProperty("deckName").GetString().Should().Be("");
    }

    [Fact]
    public async Task OnlyTheLastTargetMayHaveNoRule()
    {
        var put = await PutTargets(new
        {
            targets = new object[]
            {
                new { deckName = "A", modelName = "Lapis+", tags = "", when = new { mediaTypes = Array.Empty<int>(), titles = Array.Empty<object>() } },
                new { deckName = "B", modelName = "Lapis+", tags = "", when = (object?)null },
            },
            padStartMs = 0, padEndMs = 0
        });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutTargets(new { targets = Array.Empty<object>(), padStartMs = 0, padEndMs = 0 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A rule that holds only unknown media types or titles is empty once cleaned.
        var junk = await PutTargets(new
        {
            targets = new object[]
            {
                new { deckName = "A", modelName = "Lapis+", tags = "", when = new { mediaTypes = new[] { 999 }, titles = new[] { new { deckId = 0, title = "x" } } } },
                new { deckName = "B", modelName = "Lapis+", tags = "", when = (object?)null },
            },
            padStartMs = 0, padEndMs = 0
        });
        junk.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnOldCardTargetBecomesAListAndItsPoliciesMoveToTheSetup()
    {
        await PutSetup(Setup());
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserSettings.Add(new UserSettings
            {
                UserId = TestUsers.UserA,
                AnkiCardTargetJson = """{"deckName":"Mining","modelName":"Lapis+","tags":"jiten","padStartMs":100,"padEndMs":200,"emptyFields":{"picture":"deny","sentenceEnglish":"warn"}}"""
            });
            await userDb.SaveChangesAsync();
        }

        var target = await Get("/api/anki-sync/card-target");
        var only = target.GetProperty("targets").EnumerateArray().Should().ContainSingle().Subject;
        only.GetProperty("deckName").GetString().Should().Be("Mining");
        target.GetProperty("padEndMs").GetInt32().Should().Be(200);

        var setup = (await Get("/api/anki-sync/setups")).GetProperty("setups")[0];
        var policies = setup.GetProperty("mining").GetProperty("emptyFields");
        policies.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Should().BeEquivalentTo("picture=deny", "sentenceEnglish=warn");
        setup.GetProperty("mining").GetProperty("existing").GetString().Should().Be("ask");

        using var check = factory.Services.CreateScope();
        var json = await check.ServiceProvider.GetRequiredService<UserDbContext>().UserSettings.AsNoTracking()
                              .Where(s => s.UserId == TestUsers.UserA).Select(s => s.AnkiCardTargetJson).SingleAsync();
        json.Should().Contain("\"targets\"", "the converted target is saved, so the move happens once");
    }

    [Fact]
    public async Task ASetupKeepsItsMiningSettings()
    {
        (await Get("/api/anki-sync/setups")).GetProperty("setups").GetArrayLength().Should().Be(0);
        await PutSetup(Setup());
        (await Get("/api/anki-sync/setups")).GetProperty("setups")[0].GetProperty("mining").ValueKind.Should().Be(JsonValueKind.Null, "the defaults apply");

        var withMining = new Dictionary<string, object?>
        {
            ["modelName"] = "Lapis+", ["filter"] = null,
            ["fieldRoles"] = new Dictionary<string, string> { ["Expression"] = "word", ["Sentence"] = "sentence", ["Sentences"] = "sentenceList" },
            ["preset"] = null, ["knownWords"] = null, ["importSentences"] = true, ["takeFlags"] = true, ["foldDuplicates"] = true,
            ["updateMainFields"] = false, ["sentenceList"] = "script", ["scriptVersion"] = null,
            ["mining"] = new
            {
                existing = "auto", parts = new { audio = true, picture = false, english = true },
                emptyFields = new Dictionary<string, string> { ["sentenceAudio"] = "deny", ["sentenceEnglish"] = "warn", ["nonsense"] = "deny", ["picture"] = "maybe" }
            }
        };
        (await PutSetup(withMining)).StatusCode.Should().Be(HttpStatusCode.OK);
        var mining = (await Get("/api/anki-sync/setups")).GetProperty("setups")[0].GetProperty("mining");
        mining.GetProperty("existing").GetString().Should().Be("auto");
        mining.GetProperty("parts").GetProperty("picture").GetBoolean().Should().BeFalse();
        mining.GetProperty("emptyFields").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("sentenceAudio", "sentenceEnglish");

        // A save without mining settings keeps them.
        await PutSetup(Setup());
        (await Get("/api/anki-sync/setups")).GetProperty("setups")[0].GetProperty("mining").GetProperty("existing").GetString().Should().Be("auto");

        withMining["mining"] = new { existing = "always" };
        (await PutSetup(withMining)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NotesStoredWithoutTheirOwnSentenceAreListed()
    {
        // A Core 2000 note synced for its list only: in the store, with a mined sentence but none of its own.
        const long coreNote = 1339252521736;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var word = new MinedWord { UserId = TestUsers.UserA, Expression = "約束", Reading = "やくそく" };
            userDb.MinedWords.Add(word);
            await userDb.SaveChangesAsync();
            userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = coreNote, ModelName = "Core 2000", DeckName = "Core 2000" });
            userDb.MinedSentences.Add(new MinedSentence
            {
                MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = "分かった、<b>約束</b>する。", Origin = MinedSentenceOrigin.Jiten, SortOrder = 0
            });
            await userDb.SaveChangesAsync();
        }

        var ids = await Get("/api/mined/notes");
        ids.GetProperty("withoutSentence").EnumerateArray().Select(i => i.GetInt64()).Should().Equal(coreNote);
        ids.GetProperty("ankiNoteIds").EnumerateArray().Select(i => i.GetInt64()).Should().Equal(coreNote);

        // Importing the note's own sentence writes the central sentence table, which needs Postgres; it was checked against
        // the session database and Anki's test profile instead (run notes).
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
