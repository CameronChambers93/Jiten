using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Services.Mining;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Automatic sentences: the lines Sync adds by itself to mined words' cards.</summary>
public class MinedAutoSentenceTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Target = 7101;
    private const int KnownWord = 7102;
    private const int UnknownWord = 7103;
    private const int Particle = 7104;
    private const long AnkiNote = 1793210088120;

    private long _wordId;
    private readonly Dictionary<string, long> _lines = [];

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        int[] wordIds = [Target, KnownWord, UnknownWord, Particle];
        await jitenDb.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        await jitenDb.DeckVideoSources.ExecuteDeleteAsync();
        await jitenDb.DeckEnglishTracks.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.Where(w => wordIds.Contains(w.WordId)).ExecuteDeleteAsync();
        await userDb.MinedSentenceRejections.ExecuteDeleteAsync();
        await userDb.MinedSentences.ExecuteDeleteAsync();
        await userDb.MinedNotes.ExecuteDeleteAsync();
        await userDb.MinedWords.ExecuteDeleteAsync();
        await userDb.AnkiNoteTypeSetups.ExecuteDeleteAsync();
        await userDb.SentenceFlags.ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserSettings.ExecuteDeleteAsync();

        foreach (var wordId in wordIds)
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["noun"] });

        var show = new Deck { OriginalTitle = "Show", MediaType = MediaType.Anime, ReleaseDate = new DateOnly(2020, 1, 1) };
        var other = new Deck { OriginalTitle = "Other show", MediaType = MediaType.Anime, ReleaseDate = new DateOnly(2020, 1, 1) };
        var novel = new Deck { OriginalTitle = "Novel", MediaType = MediaType.Novel, ReleaseDate = new DateOnly(2020, 1, 1) };
        jitenDb.Decks.AddRange(show, other, novel);
        await jitenDb.SaveChangesAsync();
        var episodes = Enumerable.Range(1, 3).Select(i => new Deck
        {
            OriginalTitle = $"Show {i}", MediaType = MediaType.Anime, ParentDeckId = show.DeckId, DeckOrder = i, ReleaseDate = new DateOnly(2020, 1, 1)
        }).ToList();
        var otherEpisode = new Deck { OriginalTitle = "Other 1", MediaType = MediaType.Anime, ParentDeckId = other.DeckId, DeckOrder = 1, ReleaseDate = new DateOnly(2020, 1, 1) };
        jitenDb.Decks.AddRange(episodes);
        jitenDb.Decks.Add(otherEpisode);
        await jitenDb.SaveChangesAsync();
        foreach (var d in episodes.Append(otherEpisode))
        {
            // The planner only counts a clip whose video file exists.
            jitenDb.DeckVideoSources.Add(new DeckVideoSource { DeckId = d.DeckId, VideoPath = Path.GetTempFileName() });
            jitenDb.DeckEnglishTracks.Add(new DeckEnglishTrack { DeckId = d.DeckId, Source = "test", SavedAt = DateTime.UtcNow });
        }

        await jitenDb.SaveChangesAsync();

        SentenceToken[] Readable() => [Token(Target, 0), Token(Particle, 2, functionWord: true), Token(KnownWord, 3)];
        await Line(jitenDb, "own", episodes[0].DeckId, "運命をよく感じたんだ", Readable(), 0.05f);
        await Line(jitenDb, "ep1", episodes[0].DeckId, "運命の日がまた来たね", Readable(), 0.1f);
        await Line(jitenDb, "ep2-hard", episodes[1].DeckId, "運命のことを思い出した", Readable(), 0.3f);
        await Line(jitenDb, "ep2-easy", episodes[1].DeckId, "運命の人に会えたよね", Readable(), 0.2f);
        await Line(jitenDb, "other", otherEpisode.DeckId, "運命だと思っていたよ", Readable(), 0.5f);
        await Line(jitenDb, "unknown", episodes[2].DeckId, "運命と見知らぬ言葉です", [Token(Target, 0), Token(UnknownWord, 3)], 0.01f);
        await Line(jitenDb, "long", episodes[2].DeckId, new string('長', 45), Readable(), 0.01f);
        await Line(jitenDb, "repeat", episodes[2].DeckId, "運命をよく感じたんだ！", Readable(), 0.01f);
        await Line(jitenDb, "novel", novel.DeckId, "運命の本を読んでいた", Readable(), 0.01f);

        var now = DateTime.UtcNow;
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, KnownWord, 0, state: FsrsState.Review, stability: 5, difficulty: 5,
                                          due: now.AddDays(5), lastReview: now.AddDays(-1)));

        var word = new MinedWord { UserId = TestUsers.UserA, WordId = Target, ReadingIndex = 0, Expression = "運命", Reading = "うんめい" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();
        _wordId = word.MinedWordId;
        userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = AnkiNote, ModelName = "Lapis+" });
        userDb.MinedSentences.Add(new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Text = "<b>運命</b>をよく感じたんだ", Origin = MinedSentenceOrigin.Jiten,
            ExampleSentenceId = _lines["own"], SourceAnkiNoteId = AnkiNote, SortOrder = 0
        });
        userDb.AnkiNoteTypeSetups.Add(new AnkiNoteTypeSetup
        {
            UserId = TestUsers.UserA, ModelName = "Lapis+", FieldRolesJson = """{"Expression":"word","Sentence":"sentence","Sentences":"sentenceList"}""",
            ImportSentences = true, SentenceList = AnkiSentenceListFormat.Script,
            AutoSentencesJson = """{"enabled":true,"perCard":5,"perSync":3}"""
        });
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SentenceToken Token(int wordId, byte position, bool functionWord = false)
        => new(wordId, 0, position, 2, IsTarget: wordId == Target, IsFunctionWord: functionWord);

    private async Task Line(JitenDbContext db, string name, int deckId, string text, SentenceToken[] tokens, float difficulty)
    {
        var sentence = new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = difficulty, Tokens = ExampleSentenceTokens.Encode(tokens, false),
            WordKeys = ExampleSentenceTokens.WordKeys(tokens, 0),
        };
        db.ExampleSentences.Add(sentence);
        await db.SaveChangesAsync();
        _lines[name] = sentence.SentenceId;
    }

    private async Task<PlanPayload> Plan()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/auto/plan").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PlanPayload>())!;
    }

    private string NameOf(long sentenceId) => _lines.First(l => l.Value == sentenceId).Key;

    private Task<HttpResponseMessage> AddAuto(long exampleSentenceId, string text) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/sentences")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                ankiNoteId = AnkiNote, exampleSentenceId, sentence = text, furigana = (string?)null, english = "Fate.", englishOrigin = "subtitles",
                sourceTitle = "Show", audioFilename = $"jiten_{exampleSentenceId}.mp3", pictureFilename = (string?)null, auto = true
            }));

    [Fact]
    public async Task Plan_PicksNewTitlesThenNewEpisodesThenEasiest()
    {
        var plan = await Plan();

        plan.Words.Should().ContainSingle();
        var word = plan.Words[0];
        word.AnkiNoteId.Should().Be(AnkiNote);
        // Other show first (a title the word has no line from), then episode 2's easier line (one per episode), then episode 1.
        // Left out: a line with another unknown word, one too long, a repeat of the card's own text, and a novel's.
        word.Sentences.Select(s => NameOf(s.SentenceId)).Should().Equal("other", "ep2-easy", "ep1");
        word.Sentences.Should().AllSatisfy(s => (s.WordPosition, s.WordLength).Should().Be((0, 2)));
        plan.Capped.Should().BeFalse();
    }

    [Fact]
    public async Task Plan_StopsAtTheCardsLimit()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var setup = await userDb.AnkiNoteTypeSetups.SingleAsync();
            setup.AutoSentencesJson = """{"enabled":true,"perCard":2,"perSync":3}""";
            await userDb.SaveChangesAsync();
        }

        var plan = await Plan();

        plan.Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().Equal("other");
    }

    [Fact]
    public async Task Plan_RespectsTheLimitPerSync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserSettings.Add(new UserSettings { UserId = TestUsers.UserA, AutoSentencesMaxPerSync = 1 });
            await userDb.SaveChangesAsync();
        }

        var plan = await Plan();

        plan.Lines.Should().Be(1);
        plan.Capped.Should().BeTrue();
    }

    [Fact]
    public async Task Plan_IsEmptyWhenOffOrFlagged()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.SentenceFlags.Add(new SentenceFlag { UserId = TestUsers.UserA, ExampleSentenceId = _lines["other"] });
            await userDb.SaveChangesAsync();
        }

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().NotContain("other");

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var setup = await userDb.AnkiNoteTypeSetups.SingleAsync();
            setup.AutoSentencesJson = """{"enabled":false}""";
            await userDb.SaveChangesAsync();
        }

        (await Plan()).Words.Should().BeEmpty();
    }

    [Fact]
    public async Task AddedAutoLine_IsMarked_AndRemovingItKeepsItOff()
    {
        var response = await AddAuto(_lines["other"], "<b>運命</b>だと思っていたよ");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        long minedSentenceId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var added = await userDb.MinedSentences.SingleAsync(s => s.ExampleSentenceId == _lines["other"]);
            added.AutoAddedAt.Should().NotBeNull();
            added.SortOrder.Should().BeGreaterThan(0, "the card's own sentence stays first");
            minedSentenceId = added.MinedSentenceId;
        }

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().NotContain("other");

        var list = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/mined/words?auto=true").WithUser(TestUsers.UserA));
        (await list.Content.ReadFromJsonAsync<WordsPayload>())!.Words.Should().ContainSingle(w => w.MinedWordId == _wordId && w.Auto == 1);

        var removed = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/mined/sentences/{minedSentenceId}").WithUser(TestUsers.UserA));
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().NotContain("other");
        var again = await AddAuto(_lines["other"], "<b>運命</b>だと思っていたよ");
        (await again.Content.ReadFromJsonAsync<AddPayload>())!.Added.Should().BeFalse();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.MinedSentences.AnyAsync(s => s.ExampleSentenceId == _lines["other"])).Should().BeFalse();
            (await userDb.MinedSentenceRejections.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task AutoLine_DoesNotPromoteALineKeptForJiten()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.MinedSentences.Add(new MinedSentence
            {
                MinedWordId = _wordId, UserId = TestUsers.UserA, Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = _lines["ep1"],
                JitenOnly = true, SortOrder = 1
            });
            await userDb.SaveChangesAsync();
        }

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().NotContain("ep1");
        var response = await AddAuto(_lines["ep1"], "<b>運命</b>の日がまた来たね");
        (await response.Content.ReadFromJsonAsync<AddPayload>())!.Added.Should().BeFalse();
    }

    [Fact]
    public async Task Setup_WithAutoOnButNoSentenceList_IsRefused()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/anki-sync/setups")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                modelName = "Core 2000", filter = (string?)null, fieldRoles = new Dictionary<string, string> { ["Expression"] = "word" }, preset = (string?)null,
                knownWords = (string?)null, importSentences = false, takeFlags = false, foldDuplicates = false, updateMainFields = false, sentenceList = "none",
                scriptVersion = (int?)null, autoSentences = new { enabled = true }
            }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Plan_LeavesOutSettledWords_AndCapsOnlyWhenALineIsLeft()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserSettings.Add(new UserSettings { UserId = TestUsers.UserA, AutoSentencesMaxPerSync = 3 });
            await userDb.SaveChangesAsync();
        }

        // Exactly the three lines that qualify fit, so nothing is left for the next Sync.
        var plan = await Plan();
        plan.Lines.Should().Be(3);
        plan.Capped.Should().BeFalse();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, Target, 0, state: FsrsState.Mastered, stability: 5, difficulty: 5,
                                              due: DateTime.UtcNow.AddDays(5), lastReview: DateTime.UtcNow.AddDays(-1)));
            await userDb.SaveChangesAsync();
        }

        (await Plan()).Words.Should().BeEmpty();
    }

    [Fact]
    public async Task SkippedLine_IsNotPlannedAgain()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/mined/auto/skip")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { ankiNoteId = AnkiNote, exampleSentenceId = _lines["other"] }));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().Equal("ep2-easy", "ep1");
    }

    [Fact]
    public async Task Plan_SkipsNoteTypesWithoutASentenceList()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var setup = await userDb.AnkiNoteTypeSetups.SingleAsync();
            setup.SentenceList = AnkiSentenceListFormat.None;
            await userDb.SaveChangesAsync();
        }

        (await Plan()).Words.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_LeavesOutEpisodesWhoseVideoIsGone()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var otherDeck = await jitenDb.ExampleSentences.Where(e => e.SentenceId == _lines["other"]).Select(e => e.DeckId!.Value).SingleAsync();
            var source = await jitenDb.DeckVideoSources.SingleAsync(v => v.DeckId == otherDeck);
            source.VideoPath = "/media/tv/moved away/episode.mkv";
            await jitenDb.SaveChangesAsync();
        }

        (await Plan()).Words.Single().Sentences.Select(s => NameOf(s.SentenceId)).Should().Equal("ep2-easy", "ep1");
    }

    [Fact]
    public void Rank_SpreadsAcrossTitlesAndEpisodes()
    {
        var c = (long id, int deck, int title, float difficulty) => new MinedAutoSentenceService.Candidate(id, deck, title, $"line {id}", difficulty, 0, 1);
        var picked = MinedAutoSentenceService.Rank([c(1, 10, 1, 0.1f), c(2, 10, 1, 0.05f), c(3, 11, 1, 0.9f), c(4, 20, 2, 0.5f)],
                                                   new HashSet<int> { 1 }, new HashSet<int> { 10 }, 3);
        picked.Select(p => p.SentenceId).Should().Equal(4, 3, 2);
    }

    private record PlanPayload(List<PlanWord> Words, int Lines, int MaxPerSync, bool Capped);

    private record PlanWord(long MinedWordId, long AnkiNoteId, string ModelName, string Expression, List<PlanSentence> Sentences);

    private record PlanSentence(long SentenceId, int WordPosition, int WordLength);

    private record WordsPayload(List<WordRow> Words, int Total);

    private record WordRow(long MinedWordId, int Auto);

    private record AddPayload(bool Added);
}
