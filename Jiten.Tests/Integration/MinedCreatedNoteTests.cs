using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Recording a note the browser just created for an example sentence (n1miner's "create a new note").</summary>
public class MinedCreatedNoteTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedNotes.RemoveRange(userDb.MinedNotes);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        userDb.AnkiNoteTypeSetups.RemoveRange(userDb.AnkiNoteTypeSetups);
        await userDb.SaveChangesAsync();
        userDb.AnkiNoteTypeSetups.Add(new AnkiNoteTypeSetup
        {
            UserId = TestUsers.UserA, ModelName = "Lapis+", FieldRolesJson = """{"Expression":"word","Sentence":"sentence","Sentences":"sentenceList"}""",
            ImportSentences = true, TakeFlags = true, FoldDuplicates = true, UpdateMainFields = true, SentenceList = AnkiSentenceListFormat.Script
        });
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const long OlderNote = 1793210088120;
    private const long NewNote = 1793299999999;

    private Task<HttpResponseMessage> Created(long ankiNoteId, long exampleSentenceId, string sentence, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/notes/created")
            .WithUser(user)
            .WithJsonContent(new
            {
                ankiNoteId, modelName = "Lapis+", deckName = "Mining", wordId = 1291830, readingIndex = (byte)0, expression = "運命", reading = "うんめい",
                exampleSentenceId, sentence, furigana = (string?)null, english = "I felt it was destiny.", englishOrigin = "ai",
                sourceTitle = "オッドタクシー Episode 4", audioFilename = $"jiten_{exampleSentenceId}.mp3", pictureFilename = (string?)null
            }));

    private async Task SetTemplate(string template) =>
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/mined/settings").WithUser(TestUsers.UserA)
                                 .WithJsonContent(new { targetWordTemplate = template }))).StatusCode.Should().Be(HttpStatusCode.NoContent);

    [Fact]
    public async Task ANewWordIsRecordedAtOnceWithItsMainFieldsAsWritten()
    {
        await SetTemplate("<u>{word}</u>");

        var response = await Created(NewNote, 9693, "<b>運命</b>を感じた");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("ankiNoteId").GetInt64().Should().Be(NewNote);
        result.GetProperty("added").GetBoolean().Should().BeTrue();
        result.GetProperty("sentences").GetInt32().Should().Be(1);
        var write = result.GetProperty("write");
        write.GetProperty("sentencesHtml").GetString().Should().Contain("<div class=\"jiten-sentence-text\"><u>運命</u>を感じた</div>");
        // The new note's main fields, as Sync renders them, so the browser can correct what it wrote.
        write.GetProperty("main").GetProperty("sentence").GetString().Should().Be("<u>運命</u>を感じた");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var note = await userDb.MinedNotes.AsNoTracking().SingleAsync(n => n.AnkiNoteId == NewNote);
        var sentence = await userDb.MinedSentences.AsNoTracking().SingleAsync(s => s.MinedWordId == note.MinedWordId);
        sentence.Text.Should().Be("<b>運命</b>を感じた", "the store keeps its own form; the template is only applied on the way out");
        sentence.Origin.Should().Be(MinedSentenceOrigin.Jiten);
        sentence.EnglishOrigin.Should().Be("ai");
        sentence.SourceAnkiNoteId.Should().Be(NewNote);
        note.FieldsSentenceId.Should().Be(sentence.MinedSentenceId);
        note.FieldsHash.Should().Be(write.GetProperty("main").GetProperty("hash").GetString());
        (await userDb.MinedWords.AsNoTracking().SingleAsync(w => w.MinedWordId == note.MinedWordId)).WordId.Should().Be(1291830);
    }

    [Fact]
    public async Task ANewNoteForAWordInAnkiJoinsItAndWritesTheOlderNote()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var word = new MinedWord { UserId = TestUsers.UserA, WordId = 1291830, ReadingIndex = 0, Expression = "運命", Reading = "うんめい" };
            userDb.MinedWords.Add(word);
            await userDb.SaveChangesAsync();
            userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = OlderNote, ModelName = "Lapis+" });
            userDb.MinedSentences.Add(new MinedSentence
            {
                MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = "<b>運命</b>ってあると思いませんか？", Origin = MinedSentenceOrigin.Jiten,
                ExampleSentenceId = 9945, SourceAnkiNoteId = OlderNote, SortOrder = 0
            });
            await userDb.SaveChangesAsync();
        }

        var result = await (await Created(NewNote, 9693, "<b>運命</b>を感じた")).Content.ReadFromJsonAsync<JsonElement>();

        result.GetProperty("ankiNoteId").GetInt64().Should().Be(OlderNote);
        result.GetProperty("sentences").GetInt32().Should().Be(2);
        var write = result.GetProperty("write");
        write.GetProperty("ankiNoteId").GetInt64().Should().Be(OlderNote);
        write.GetProperty("sentencesHtml").GetString().Should().Contain("を感じた");
        write.GetProperty("main").ValueKind.Should().Be(JsonValueKind.Null, "the older note still shows its own sentence, the main one");
    }

    [Fact]
    public async Task ANewNoteForALineTheWordHasShowsThatSentence()
    {
        long wordId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var word = new MinedWord { UserId = TestUsers.UserA, WordId = 1291830, ReadingIndex = 0, Expression = "運命", Reading = "うんめい" };
            userDb.MinedWords.Add(word);
            await userDb.SaveChangesAsync();
            wordId = word.MinedWordId;
            userDb.MinedNotes.Add(new MinedNote { MinedWordId = wordId, UserId = TestUsers.UserA, AnkiNoteId = OlderNote, ModelName = "Lapis+" });
            userDb.MinedSentences.Add(new MinedSentence
            {
                MinedWordId = wordId, UserId = TestUsers.UserA, Text = "<b>運命</b>を感じた", Origin = MinedSentenceOrigin.Jiten,
                ExampleSentenceId = 9693, SourceAnkiNoteId = OlderNote, SortOrder = 0
            });
            await userDb.SaveChangesAsync();
        }

        var result = await (await Created(NewNote, 9693, "<b>運命</b>を感じた")).Content.ReadFromJsonAsync<JsonElement>();

        result.GetProperty("added").GetBoolean().Should().BeFalse();
        result.GetProperty("sentences").GetInt32().Should().Be(1);
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.MinedSentences.CountAsync(s => s.MinedWordId == wordId)).Should().Be(1);
        var note = await db.MinedNotes.AsNoTracking().SingleAsync(n => n.AnkiNoteId == NewNote);
        note.MinedWordId.Should().Be(wordId);
        note.FieldsSentenceId.Should().Be(result.GetProperty("minedSentenceId").GetInt64());
    }

    [Fact]
    public async Task MainFieldsDoNotComeAlongWhenTheSetupLeavesThemAlone()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.AnkiNoteTypeSetups.SingleAsync()).UpdateMainFields = false;
            await userDb.SaveChangesAsync();
        }

        var write = (await (await Created(NewNote, 9693, "<b>運命</b>を感じた")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("write");

        write.GetProperty("sentencesHtml").GetString().Should().Contain("を感じた");
        write.GetProperty("main").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RecordingTheSameNoteAgainChangesNothing()
    {
        (await Created(NewNote, 9693, "<b>運命</b>を感じた")).StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await (await Created(NewNote, 9693, "<b>運命</b>を感じた")).Content.ReadFromJsonAsync<JsonElement>();

        again.GetProperty("added").GetBoolean().Should().BeFalse();
        again.GetProperty("sentences").GetInt32().Should().Be(1);
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.MinedSentences.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AnEmptySentenceOrWordIsRefused()
    {
        (await Created(NewNote, 9693, "<b></b>")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noWord = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/notes/created").WithUser(TestUsers.UserA)
            .WithJsonContent(new { ankiNoteId = NewNote, modelName = "Lapis+", deckName = "Mining", expression = "", reading = "", exampleSentenceId = 1, sentence = "<b>a</b>" }));
        noWord.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
