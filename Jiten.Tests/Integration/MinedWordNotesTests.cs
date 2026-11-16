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

/// <summary>
/// Finding a mined word's notes by its dictionary entry, not its spelling (GDR-061): どう "how" (1008910, reading 2) and どう
/// "whoa" (2255350, reading 0) share a spelling.
/// </summary>
public class MinedWordNotesTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int How = 1008910;
    private const byte HowReading = 2;
    private const int Whoa = 2255350;

    private const long CoreNote = 1339252520857;
    private const long LapisNote = 1793579776657;
    private const long FoldingNote = 1793579776700;
    private const long WhoaNote = 1793600000000;
    private const long NoEntryNote = 1793600000100;
    private const long Unstored = 1793700000000;

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
            ImportSentences = true, TakeFlags = true, FoldDuplicates = true, SentenceList = AnkiSentenceListFormat.Script
        });
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<MinedWord> Word(int? wordId, byte? readingIndex, params (long Id, string Model, string? Match, long? FoldingInto)[] notes)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = new MinedWord { UserId = TestUsers.UserA, WordId = wordId, ReadingIndex = readingIndex, Expression = "どう", Reading = "どう" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();
        foreach (var n in notes)
            userDb.MinedNotes.Add(new MinedNote
            {
                MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = n.Id, ModelName = n.Model, WordMatch = n.Match,
                FoldingInto = n.FoldingInto
            });
        await userDb.SaveChangesAsync();
        return word;
    }

    private async Task<JsonElement> WordNotes(int wordId, byte readingIndex, long[] candidates, string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/word-notes")
                                                   .WithUser(user)
                                                   .WithJsonContent(new { wordId, readingIndex, candidates }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static long[] Ids(JsonElement list, string? property = "ankiNoteId") =>
        list.EnumerateArray().Select(e => property == null ? e.GetInt64() : e.GetProperty(property).GetInt64()).ToArray();

    [Fact]
    public async Task SortsTheSpellingMatchesByEntry()
    {
        await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null), (CoreNote, "Core 2000", MinedWordMatch.Gloss, null),
                   (FoldingNote, "Lapis+", MinedWordMatch.Single, LapisNote));
        await Word(null, null, (NoEntryNote, "Lapis+", MinedWordMatch.None, null));

        var result = await WordNotes(Whoa, 0, [LapisNote, CoreNote, FoldingNote, NoEntryNote, Unstored]);

        Ids(result.GetProperty("own")).Should().BeEmpty();
        // Oldest first; the note being folded away is left out.
        Ids(result.GetProperty("others")).Should().Equal(CoreNote, LapisNote, NoEntryNote);
        var core = result.GetProperty("others")[0];
        core.GetProperty("wordId").GetInt32().Should().Be(How);
        core.GetProperty("readingIndex").GetInt32().Should().Be(HowReading);
        core.GetProperty("modelName").GetString().Should().Be("Core 2000");
        core.GetProperty("wordMatch").GetString().Should().Be(MinedWordMatch.Gloss);
        result.GetProperty("others")[2].GetProperty("wordId").ValueKind.Should().Be(JsonValueKind.Null);
        Ids(result.GetProperty("unstored"), null).Should().Equal(Unstored);
    }

    [Fact]
    public async Task OwnNotesComeFromTheStoreEvenWhenTheSearchMissedThem()
    {
        await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null), (CoreNote, "Core 2000", MinedWordMatch.Gloss, null),
                   (FoldingNote, "Lapis+", MinedWordMatch.Single, CoreNote));
        await Word(Whoa, 0, (WhoaNote, "Lapis+", MinedWordMatch.Guessed, null));

        var result = await WordNotes(How, HowReading, [LapisNote, WhoaNote]);

        Ids(result.GetProperty("own")).Should().Equal(CoreNote, LapisNote);
        Ids(result.GetProperty("others")).Should().Equal(WhoaNote);
        result.GetProperty("others")[0].GetProperty("wordMatch").GetString().Should().Be(MinedWordMatch.Guessed);
        Ids(result.GetProperty("unstored"), null).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherUsersNotesAreNotStoredForThisOne()
    {
        await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null));

        var result = await WordNotes(How, HowReading, [LapisNote], TestUsers.UserB);

        Ids(result.GetProperty("own")).Should().BeEmpty();
        Ids(result.GetProperty("others")).Should().BeEmpty();
        Ids(result.GetProperty("unstored"), null).Should().Equal(LapisNote);
    }

    [Fact]
    public async Task TooManyCandidatesAreRefused()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/word-notes")
                                                   .WithUser(TestUsers.UserA)
                                                   .WithJsonContent(new { wordId = How, readingIndex = HowReading, candidates = Enumerable.Range(1, 201).Select(i => (long)i) }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> AddSentence(long ankiNoteId, long exampleSentenceId, int? parsedWordId, byte? parsedReadingIndex) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/sentences")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                ankiNoteId, exampleSentenceId, sentence = "ほら、<b>どう</b>？これも、着てみていい？", furigana = (string?)null, english = "How's this?",
                englishOrigin = "subtitles", sourceTitle = (string?)null, audioFilename = (string?)null, pictureFilename = (string?)null,
                parsedWordId, parsedReadingIndex
            }));

    private async Task KeepForJiten(MinedWord word, long exampleSentenceId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.MinedSentences.Add(new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = exampleSentenceId,
            Text = "ほら、どう？これも、着てみていい？", JitenOnly = true
        });
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task AddingToAnotherEntrysNoteTakesTheLineOffTheParsedWord()
    {
        var how = await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null));
        var whoa = await Word(Whoa, 0);
        await KeepForJiten(whoa, 4242);

        var response = await AddSentence(LapisNote, 4242, Whoa, 0);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var lines = await userDb.MinedSentences.AsNoTracking().Where(s => s.ExampleSentenceId == 4242).ToListAsync();
        lines.Should().ContainSingle().Which.MinedWordId.Should().Be(how.MinedWordId);
        lines[0].JitenOnly.Should().BeFalse();
        // The parsed word had nothing else, so it goes too.
        (await userDb.MinedWords.AnyAsync(w => w.MinedWordId == whoa.MinedWordId)).Should().BeFalse();
    }

    [Fact]
    public async Task TheParsedWordStaysWhenItHasMore()
    {
        await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null));
        var whoa = await Word(Whoa, 0);
        await KeepForJiten(whoa, 4242);
        await KeepForJiten(whoa, 4343);

        (await AddSentence(LapisNote, 4242, Whoa, 0)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var left = await userDb.MinedSentences.AsNoTracking().Where(s => s.MinedWordId == whoa.MinedWordId).Select(s => s.ExampleSentenceId).ToListAsync();
        left.Should().Equal(4343L);
    }

    [Fact]
    public async Task WithoutAParsedWordNothingElseChanges()
    {
        await Word(How, HowReading, (LapisNote, "Lapis+", MinedWordMatch.Mined, null));
        var whoa = await Word(Whoa, 0);
        await KeepForJiten(whoa, 4242);

        (await AddSentence(LapisNote, 4242, null, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.MinedSentences.CountAsync(s => s.MinedWordId == whoa.MinedWordId && s.JitenOnly)).Should().Be(1);
    }
}
