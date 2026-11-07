using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>The word's sentence list (n1miner): saving a line for Jiten, mining it to Anki later, and study reading the list.</summary>
public class WordListTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Word = 7101;
    private const long NewNote = 1793299999999;
    private long _felt;
    private long _exists;
    /// <summary>A line whose tokens don't hold the word, so only the list can offer it to study.</summary>
    private long _untokened;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.MinedSentences.ExecuteDeleteAsync();
        await userDb.MinedNotes.ExecuteDeleteAsync();
        await userDb.MinedWords.ExecuteDeleteAsync();
        await userDb.AnkiNoteTypeSetups.ExecuteDeleteAsync();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();
        await userDb.SentenceFlags.ExecuteDeleteAsync();
        userDb.AnkiNoteTypeSetups.Add(new AnkiNoteTypeSetup
        {
            UserId = TestUsers.UserA, ModelName = "Lapis+", FieldRolesJson = """{"Expression":"word","Sentence":"sentence","Sentences":"sentenceList"}""",
            ImportSentences = true, TakeFlags = true, FoldDuplicates = true, UpdateMainFields = true, SentenceList = AnkiSentenceListFormat.Script
        });
        await userDb.SaveChangesAsync();

        await jitenDb.ExampleSentences.ExecuteDeleteAsync();
        await jitenDb.WordForms.Where(f => f.WordId == Word).ExecuteDeleteAsync();
        await jitenDb.JMDictWords.Where(w => w.WordId == Word).ExecuteDeleteAsync();
        jitenDb.JMDictWords.Add(new JmDictWord { WordId = Word, PartsOfSpeech = ["noun"] });
        jitenDb.WordForms.Add(new JmDictWordForm
        {
            WordId = Word, ReadingIndex = 0, Text = "運命", RubyText = "運命[うんめい]", FormType = JmDictFormType.KanjiForm
        });
        var deck = new Deck { OriginalTitle = "オッドタクシー", MediaType = MediaType.Anime, ReleaseDate = new DateOnly(2021, 4, 5) };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        _felt = await AddLine(jitenDb, deck.DeckId, "運命を感じた");
        _exists = await AddLine(jitenDb, deck.DeckId, "運命ってあると思いませんか");
        _untokened = await AddLine(jitenDb, deck.DeckId, "運命かもね", tokened: false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> AddLine(JitenDbContext db, int deckId, string text, bool tokened = true)
    {
        SentenceToken[] tokens = tokened ? [new(Word, 0, 0, 2, IsTarget: true, IsFunctionWord: false)] : [];
        var sentence = new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = 0.2f, Tokens = ExampleSentenceTokens.Encode(tokens, false),
            WordKeys = ExampleSentenceTokens.WordKeys(tokens, Random.Shared.Next(ExampleSentenceTokens.FineBucketCount)),
        };
        db.ExampleSentences.Add(sentence);
        await db.SaveChangesAsync();
        return sentence.SentenceId;
    }

    private Task<HttpResponseMessage> Save(long sentenceId, int position = 0, int length = 2) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/list").WithUser(TestUsers.UserA)
                              .WithJsonContent(new { wordId = Word, readingIndex = (byte)0, exampleSentenceId = sentenceId, position, length }));

    private async Task<List<JsonElement>> States(params long[] sentenceIds)
    {
        var lines = string.Join(',', sentenceIds.Select(id => $"{Word}:0:{id}"));
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/mined/list/state?lines={lines}").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
    }

    private Task<HttpResponseMessage> Created(long exampleSentenceId, string sentence) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/notes/created").WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                ankiNoteId = NewNote, modelName = "Lapis+", deckName = "Mining", wordId = Word, readingIndex = (byte)0, expression = "運命", reading = "うんめい",
                exampleSentenceId, sentence, furigana = (string?)null, english = "I felt it was destiny.", englishOrigin = "ai",
                sourceTitle = "オッドタクシー", audioFilename = $"jiten_{exampleSentenceId}.mp3", pictureFilename = (string?)null
            }));

    [Fact]
    public async Task SavingALineMakesTheWordsListKeptForJiten()
    {
        var response = await Save(_felt);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("added").GetBoolean().Should().BeTrue();
        result.GetProperty("state").GetString().Should().Be("kept");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = await userDb.MinedWords.AsNoTracking().Include(w => w.Notes).Include(w => w.Sentences).SingleAsync();
        word.WordId.Should().Be(Word);
        word.Expression.Should().Be("運命");
        word.Reading.Should().Be("うんめい");
        word.Notes.Should().BeEmpty();
        var sentence = word.Sentences.Single();
        sentence.JitenOnly.Should().BeTrue();
        sentence.Text.Should().BeNull("the text is read from the line");
        sentence.ExampleSentenceId.Should().Be(_felt);
        (sentence.TargetPosition, sentence.TargetLength).Should().Be((0, 2));

        var again = await (await Save(_felt)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("added").GetBoolean().Should().BeFalse();
        (await userDb.MinedSentences.CountAsync()).Should().Be(1);

        var states = await States(_felt, _exists);
        states.Should().ContainSingle();
        states[0].GetProperty("sentenceId").GetInt64().Should().Be(_felt);
        states[0].GetProperty("state").GetString().Should().Be("kept");
    }

    [Fact]
    public async Task AWordOutsideTheLineIsRefused()
    {
        (await Save(_felt, position: 5, length: 3)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(999_999_999)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MiningAKeptLineToTheFirstNoteLeadsTheCardAndLeavesTheOtherKeptLineOff()
    {
        (await Save(_exists)).EnsureSuccessStatusCode();
        (await Save(_felt)).EnsureSuccessStatusCode();

        var response = await Created(_felt, "<b>運命</b>を感じた");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("added").GetBoolean().Should().BeTrue("the kept line went onto the card");
        var write = result.GetProperty("write");
        var list = write.GetProperty("sentencesHtml").GetString();
        list.Should().Contain("を感じた");
        list.Should().NotContain("ってあると", "a line kept for Jiten stays off the card");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var sentences = await userDb.MinedSentences.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync();
        sentences.Should().HaveCount(2);
        var felt = sentences[0];
        felt.ExampleSentenceId.Should().Be(_felt, "the first card's sentence leads the list");
        felt.JitenOnly.Should().BeFalse();
        felt.English.Should().Be("I felt it was destiny.");
        felt.AnkiAudioFilename.Should().Be($"jiten_{_felt}.mp3");
        felt.SourceAnkiNoteId.Should().Be(NewNote);
        sentences[1].JitenOnly.Should().BeTrue();

        var states = (await States(_felt, _exists)).ToDictionary(s => s.GetProperty("sentenceId").GetInt64(), s => s.GetProperty("state").GetString());
        states[_felt].Should().Be("card");
        states[_exists].Should().Be("kept");
    }

    [Fact]
    public async Task SyncLeavesKeptLinesOffTheCard()
    {
        (await Save(_felt)).EnsureSuccessStatusCode();
        (await Created(_felt, "<b>運命</b>を感じた")).EnsureSuccessStatusCode();
        (await Save(_exists)).EnsureSuccessStatusCode();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mined/sync/notes").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("を感じた");
        body.Should().NotContain("ってあると");
    }

    [Fact]
    public async Task StudyShowsALineFromTheWordsList()
    {
        (await Save(_untokened, position: 0, length: 2)).EnsureSuccessStatusCode();

        for (var i = 0; i < 8; i++)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-examples").WithUser(TestUsers.UserA)
                                                       .WithJsonContent(new { pairs = new[] { new { wordId = Word, readingIndex = 0 } } }));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var example = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("examples").GetProperty($"{Word}-0");
            example.GetProperty("sentenceId").GetInt64().Should().Be(_untokened, "the word's list comes before the corpus");
            example.GetProperty("wordPosition").GetInt32().Should().Be(0);
            example.GetProperty("wordLength").GetInt32().Should().Be(2);
            example.GetProperty("sourceDeck").GetProperty("originalTitle").GetString().Should().Be("オッドタクシー");
        }
    }

    [Fact]
    public async Task StudySkipsAFlaggedListLine()
    {
        (await Save(_untokened)).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.SentenceFlags.Add(new SentenceFlag { UserId = TestUsers.UserA, ExampleSentenceId = _untokened });
            await userDb.SaveChangesAsync();
        }

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-examples").WithUser(TestUsers.UserA)
                                                   .WithJsonContent(new { pairs = new[] { new { wordId = Word, readingIndex = 0 } } }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var examples = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("examples");
        if (examples.TryGetProperty($"{Word}-0", out var example))
            example.GetProperty("sentenceId").GetInt64().Should().NotBe(_untokened);
    }
}
