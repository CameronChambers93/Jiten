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

/// <summary>Adding an example sentence to a word that already has an Anki note (n1miner's "add to the existing card").</summary>
public class MinedAddSentenceTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        // The factory's reset doesn't know the sentence store.
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedNotes.RemoveRange(userDb.MinedNotes);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const long FirstNote = 1793210088120;
    private const long SecondNote = 1793299999999;

    private long? _wordId;

    /// <summary>A note of 運命 as an import leaves it (the import itself runs the parser, which needs Redis).</summary>
    private async Task ImportNote(long ankiNoteId, string sentence, string audio)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = _wordId == null ? null : await userDb.MinedWords.FindAsync(_wordId.Value);
        if (word == null)
        {
            word = new MinedWord { UserId = TestUsers.UserA, Expression = "運命", Reading = "うんめい" };
            userDb.MinedWords.Add(word);
            await userDb.SaveChangesAsync();
            _wordId = word.MinedWordId;
        }

        userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = ankiNoteId, ModelName = "Lapis+" });
        var sortOrder = await userDb.MinedSentences.CountAsync(s => s.MinedWordId == word.MinedWordId);
        userDb.MinedSentences.Add(new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = sentence, Origin = MinedSentenceOrigin.Jiten,
            ExampleSentenceId = long.Parse(audio["jiten_".Length..audio.IndexOf('.')]), AnkiAudioFilename = audio, SourceAnkiNoteId = ankiNoteId,
            IsMain = sortOrder == 0, SortOrder = sortOrder
        });
        await userDb.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> AddSentence(long ankiNoteId, long exampleSentenceId, string sentence, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/sentences")
            .WithUser(user)
            .WithJsonContent(new
            {
                ankiNoteId, exampleSentenceId, sentence, furigana = "運命[うんめい]を 感[かん]じた", english = "I felt it was destiny.", englishOrigin = "ai",
                sourceTitle = "オッドタクシー Episode 4", audioFilename = $"jiten_{exampleSentenceId}.ogg", pictureFilename = (string?)null
            }));

    [Fact]
    public async Task AddsTheSentenceToTheWordAndRendersTheKeptNote()
    {
        await ImportNote(FirstNote, "<b>運命</b>ってあると思いませんか？", "jiten_9945.mp3");
        await ImportNote(SecondNote, "捨てられる<b>運命</b>ざんす。", "jiten_8298.mp3");

        // Any of the word's notes finds it; the field goes to the oldest.
        var response = await AddSentence(SecondNote, 9693, "<b>運命</b>を感じた");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("ankiNoteId").GetInt64().Should().Be(FirstNote);
        result.GetProperty("added").GetBoolean().Should().BeTrue();
        result.GetProperty("sentences").GetInt32().Should().Be(3);
        var html = result.GetProperty("sentencesHtml").GetString()!;
        html.Should().StartWith($"<div class=\"jiten-sentences\" data-note=\"{FirstNote}\">");
        html.Should().Contain("<div class=\"jiten-sentence-text\"><b>運命</b>を感じた</div>");
        html.Should().Contain("data-source=\"オッドタクシー Episode 4\"");
        html.Should().Contain("<audio class=\"jiten-sentence-audio\" src=\"jiten_9693.ogg\"");
        html.IndexOf("を感じた", StringComparison.Ordinal).Should().BeGreaterThan(html.IndexOf("ざんす", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddingTheSameSentenceAgainChangesNothing()
    {
        await ImportNote(FirstNote, "<b>運命</b>ってあると思いませんか？", "jiten_9945.mp3");

        // The note's own sentence, by example sentence id; then a new one twice.
        var own = await (await AddSentence(FirstNote, 9945, "<b>運命</b>ってあると思いませんか？")).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("added").GetBoolean().Should().BeFalse();
        own.GetProperty("sentences").GetInt32().Should().Be(1);

        (await AddSentence(FirstNote, 9693, "<b>運命</b>を感じた")).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await (await AddSentence(FirstNote, 9693, "<b>運命</b>を感じた")).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("added").GetBoolean().Should().BeFalse();
        again.GetProperty("sentences").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task ANoteNotInTheStoreIsNotFound()
    {
        (await AddSentence(FirstNote, 9693, "<b>運命</b>を感じた")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Another user's note counts as not in the store.
        await ImportNote(FirstNote, "<b>運命</b>ってあると思いませんか？", "jiten_9945.mp3");
        (await AddSentence(FirstNote, 9693, "<b>運命</b>を感じた", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnEmptySentenceIsRefused()
    {
        await ImportNote(FirstNote, "<b>運命</b>ってあると思いませんか？", "jiten_9945.mp3");
        (await AddSentence(FirstNote, 9693, "<b></b>")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WordSummariesRefuseMalformedWords()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mined/word-summaries?words=12:x").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
