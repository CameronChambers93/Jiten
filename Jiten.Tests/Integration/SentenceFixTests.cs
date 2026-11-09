using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Api.Services;
using Jiten.Api.Services.Translation;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Fixing sentences: a user's own fix, what it changes for them and nobody else, the Sync step's media names, and submitting.</summary>
public class SentenceFixTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);
    private const string Line = "えっ、なに、その頭！";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFixes.RemoveRange(userDb.SentenceFixes);
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        userDb.MinedSentences.RemoveRange(userDb.MinedSentences);
        userDb.MinedNotes.RemoveRange(userDb.MinedNotes);
        userDb.MinedWords.RemoveRange(userDb.MinedWords);
        await userDb.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.SentenceCorrections.ExecuteDeleteAsync();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Sentences.ExecuteDeleteAsync();
        db.Decks.AddRange(new Deck { DeckId = 1, OriginalTitle = "Osomatsu-san", MediaType = MediaType.Anime },
                          new Deck { DeckId = 2, OriginalTitle = "Episode 14", MediaType = MediaType.Anime, ParentDeckId = 1 },
                          new Deck { DeckId = 3, OriginalTitle = "Episode 3", MediaType = MediaType.Anime, ParentDeckId = 1 },
                          new Deck { DeckId = 4, OriginalTitle = "A book", MediaType = MediaType.Novel });
        db.DeckVideoSources.Add(new DeckVideoSource { DeckId = 2, VideoPath = "/media/ep14.mkv", SubtitleOffsetMs = 0 });
        db.Sentences.Add(new Sentence { Id = 10, TextHash = [1], Text = Line });
        db.ExampleSentences.AddRange(
            new ExampleSentence { SentenceId = 1, DeckId = 2, Position = 1, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 2, DeckId = 3, Position = 9, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 3, DeckId = 4, Text = "本の行", Tokens = NoWords, WordKeys = [] },
            new ExampleSentence { SentenceId = 4, Text = "ゲームの行", Tokens = NoWords, WordKeys = [], OwnerId = TestUsers.UserB });
        db.DeckSubtitleTracks.Add(new DeckSubtitleTrack
        {
            DeckId = 2,
            CuesJson = """[{"s":1000,"e":2000,"t":"トト子ちゃ～ん！"},{"s":2500,"e":4000,"t":"えっ、なに、その頭！"},{"s":4500,"e":6000,"t":"いや～ん"}]"""
        });
        db.DeckEnglishTracks.Add(new DeckEnglishTrack
        {
            DeckId = 2, Source = "test",
            CuesJson = """[{"s":1000,"e":2000,"t":"Totoko!"},{"s":2500,"e":4000,"t":"Huh? What's with your hair?"},{"s":4500,"e":6000,"t":"Ugh."}]"""
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string user = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(method, url);
        if (user == TestUsers.Admin) request.WithAdmin();
        else request.WithUser(user);
        if (body != null) request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Save(long id, object fix, string user = TestUsers.UserA) => Send(HttpMethod.Put, $"/api/sentences/{id}/fix", fix, user);

    private static object Fix(string? text = null, string? english = null, int? startMs = null, int? endMs = null, int? pictureMs = null) =>
        new { text, english, startMs, endMs, pictureMs };

    private async Task<JsonElement> Json(HttpMethod method, string url, string user = TestUsers.UserA)
    {
        var response = await Send(method, url, null, user);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> SentenceText(long id, string user) =>
        (await Json(HttpMethod.Get, $"/api/sentences/{id}", user)).GetProperty("sentence").GetProperty("text").GetString();

    /// <summary>A word on an Anki note with these lines; returns the mined sentences' ids.</summary>
    private async Task<List<long>> Mine(string expression, params long[] lines)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = new MinedWord { UserId = TestUsers.UserA, Expression = expression, Reading = "" };
        userDb.MinedWords.Add(word);
        await userDb.SaveChangesAsync();
        userDb.MinedNotes.Add(new MinedNote { MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, AnkiNoteId = 1793210088120 + word.MinedWordId, ModelName = "Lapis+" });
        var sentences = lines.Select((line, i) => new MinedSentence
        {
            MinedWordId = word.MinedWordId, UserId = TestUsers.UserA, Origin = MinedSentenceOrigin.Jiten, ExampleSentenceId = line, SortOrder = i,
            TargetPosition = 8, TargetLength = 1, AnkiAudioFilename = $"jiten_{line}.mp3", AnkiPictureFilename = $"jiten_{line}.jpg"
        }).ToList();
        userDb.MinedSentences.AddRange(sentences);
        await userDb.SaveChangesAsync();
        return sentences.Select(s => s.MinedSentenceId).ToList();
    }

    private async Task<T> InUserDb<T>(Func<UserDbContext, Task<T>> read)
    {
        using var scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<UserDbContext>());
    }

    [Fact]
    public async Task AFixedTextShowsForItsUserOnly()
    {
        (await Save(1, Fix(text: "えっ、なに、その髪！"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var mine = (await Json(HttpMethod.Get, "/api/sentences/1")).GetProperty("sentence");
        mine.GetProperty("text").GetString().Should().Be("えっ、なに、その髪！");
        mine.GetProperty("fixed").GetString().Should().Be("mine");
        (await SentenceText(1, TestUsers.UserB)).Should().Be(Line);
    }

    [Fact]
    public async Task SavingClosesTheFlagAndMarksMinedSentencesForSync()
    {
        var mined = await Mine("頭", 1);
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "japanese" });

        (await Save(1, Fix(text: "えっ、なに、その髪！"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var flag = await InUserDb(db => db.SentenceFlags.SingleAsync());
        flag.Resolution.Should().Be(SentenceFlagResolution.Fixed);
        (await InUserDb(db => db.MinedSentences.SingleAsync(s => s.MinedSentenceId == mined[0]))).NeedsRebuild.Should().BeTrue();
    }

    [Fact]
    public async Task AMinedSentenceShowsTheFixedTextWithTheWordStillMarked()
    {
        var mined = await Mine("頭", 1);
        await Save(1, Fix(text: "ええっ、なに、その頭！"));

        var word = await InUserDb(db => db.MinedSentences.Where(s => s.MinedSentenceId == mined[0]).Select(s => s.MinedWordId).SingleAsync());
        var body = await Json(HttpMethod.Get, $"/api/mined/words/{word}");
        body.GetProperty("sentences")[0].GetProperty("text").GetString().Should().Be("ええっ、なに、その<b>頭</b>！");
    }

    [Fact]
    public async Task FixedEnglishComesBeforeTheSubtitles()
    {
        (await Json(HttpMethod.Get, "/api/sentence-translation/1")).GetProperty("translation").GetProperty("origin").GetString().Should().Be("subtitles");

        await Save(1, Fix(english: "Hey, what's with your hair?"));

        var translation = (await Json(HttpMethod.Get, "/api/sentence-translation/1")).GetProperty("translation");
        translation.GetProperty("text").GetString().Should().Be("Hey, what's with your hair?");
        translation.GetProperty("origin").GetString().Should().Be("user");
        (await Json(HttpMethod.Get, "/api/sentence-translation/1", TestUsers.UserB)).GetProperty("translation").GetProperty("origin").GetString()
            .Should().Be("subtitles");
    }

    [Fact]
    public async Task TheEditorShowsTheOriginalTheCuesAndTheMinedSentences()
    {
        await Mine("頭", 1);
        await Save(1, Fix(startMs: 2300, endMs: 4250));

        var editor = await Json(HttpMethod.Get, "/api/sentences/1/fix");

        editor.GetProperty("title").GetString().Should().Be("Osomatsu-san");
        editor.GetProperty("episode").GetString().Should().Be("Episode 14");
        editor.GetProperty("hasVideo").GetBoolean().Should().BeTrue();
        var original = editor.GetProperty("original");
        (original.GetProperty("startMs").GetInt32(), original.GetProperty("endMs").GetInt32()).Should().Be((2250, 4250));
        original.GetProperty("english").GetString().Should().Be("Huh? What's with your hair?");
        editor.GetProperty("mine").GetProperty("startMs").GetInt32().Should().Be(2300);
        editor.GetProperty("cues").EnumerateArray().Select(c => (c.GetProperty("text").GetString(), c.GetProperty("inClip").GetBoolean()))
              .Should().Equal(("トト子ちゃ～ん！", false), (Line, true), ("いや～ん", false));
        editor.GetProperty("sameText").EnumerateArray().Select(s => s.GetProperty("sentenceId").GetInt64()).Should().Equal(2);
        editor.GetProperty("mined")[0].GetProperty("removable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SuggestsTheEnglishOfTheCuesAClipCovers()
    {
        var body = await Json(HttpMethod.Get, "/api/sentences/1/fix/english?startMs=750&endMs=4250");

        body.GetProperty("text").GetString().Should().Be("Totoko! Huh? What's with your hair?");
    }

    [Fact]
    public async Task OnlyLinesWithAVideoCanHaveTheirClipFixed()
    {
        var response = await Save(3, Fix(startMs: 0, endMs: 1000));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(3, Fix(english: "A line from a book."))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData(2000, null)]
    [InlineData(3000, 2000)]
    [InlineData(1000, 40000)]
    public async Task ABadClipIsRefused(int? startMs, int? endMs)
    {
        (await Save(1, Fix(startMs: startMs, endMs: endMs))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnotherUsersPrivateLineCantBeFixed()
    {
        (await Save(4, Fix(text: "別の行"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, "/api/sentences/4/fix")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResettingDeletesTheFix()
    {
        await Save(1, Fix(text: "えっ、なに、その髪！"));

        (await Send(HttpMethod.Delete, "/api/sentences/1/fix")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await InUserDb(db => db.SentenceFixes.CountAsync())).Should().Be(0);
        (await SentenceText(1, TestUsers.UserA)).Should().Be(Line);
    }

    [Fact]
    public async Task AFixedClipGivesTheMediaNewNamesForSync()
    {
        var mined = await Mine("頭", 1);
        (await Json(HttpMethod.Get, "/api/mined/media")).GetArrayLength().Should().Be(0);

        await Save(1, Fix(startMs: 2300, endMs: 4250));

        var behind = (await Json(HttpMethod.Get, "/api/mined/media")).EnumerateArray().Single();
        behind.GetProperty("minedSentenceId").GetInt64().Should().Be(mined[0]);
        var audio = behind.GetProperty("audio").GetString()!;
        audio.Should().MatchRegex(@"^jiten_1_[0-9a-f]{8}\.mp3$");
        behind.GetProperty("picture").GetString().Should().MatchRegex(@"^jiten_1_[0-9a-f]{8}\.jpg$");
        behind.GetProperty("video").ValueKind.Should().Be(JsonValueKind.Null);

        // The page's sentence carries the same key, so mining it again stores it under the same name
        var sentence = (await Json(HttpMethod.Get, "/api/sentences/1")).GetProperty("sentence");
        $"jiten_1{sentence.GetProperty("clipKey").GetString()}.mp3".Should().Be(audio);

        (await Send(HttpMethod.Post, $"/api/mined/sentences/{mined[0]}/media", new { audio, picture = behind.GetProperty("picture").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Json(HttpMethod.Get, "/api/mined/media")).GetArrayLength().Should().Be(0);

        // Resetting the clip puts the cues' names back
        await Send(HttpMethod.Delete, "/api/sentences/1/fix");
        (await Json(HttpMethod.Get, "/api/mined/media"))[0].GetProperty("audio").GetString().Should().Be("jiten_1.mp3");
    }

    [Fact]
    public async Task OnlyNamesJitenGivesAreRecorded()
    {
        var mined = await Mine("頭", 1);

        (await Send(HttpMethod.Post, $"/api/mined/sentences/{mined[0]}/media", new { audio = "../evil.mp3" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SubmittingMakesTheFixEveryonesAndKeepsTheSubtitleText()
    {
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "japanese" }, TestUsers.UserB);
        await Save(1, Fix(text: "えっ、なに、その髪！", english: "Hey, your hair!", startMs: 2300, endMs: 4250), TestUsers.Admin);

        var submitted = await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { sameTextIds = new[] { 2L } }, TestUsers.Admin);
        submitted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean().Should().BeTrue();

        (await SentenceText(1, TestUsers.UserB)).Should().Be("えっ、なに、その髪！");
        (await SentenceText(2, TestUsers.UserB)).Should().Be("えっ、なに、その髪！");
        (await Json(HttpMethod.Get, "/api/sentences/1", TestUsers.UserB)).GetProperty("sentence").GetProperty("fixed").GetString().Should().Be("shared");
        (await Json(HttpMethod.Get, "/api/sentence-translation/1", TestUsers.UserB)).GetProperty("translation").GetProperty("text").GetString()
            .Should().Be("Hey, your hair!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var line = await db.ExampleSentences.SingleAsync(s => s.SentenceId == 1);
        (line.SourceText, line.StartMs, line.EndMs).Should().Be((Line, 2300, 4250));
        ExampleSentenceTokens.Decode(line.Tokens).Select(t => t.WordId).Should().Equal(1002);
        (await db.ExampleSentences.SingleAsync(s => s.SentenceId == 2)).StartMs.Should().BeNull();
        (await db.SentenceCorrections.CountAsync()).Should().Be(2);

        (await InUserDb(db => db.SentenceFixes.CountAsync())).Should().Be(0);
        (await InUserDb(db => db.SentenceFlags.SingleAsync())).Resolution.Should().Be(SentenceFlagResolution.Fixed);

        // The cue lookup still finds the line by its subtitle text, so the editor shows its cues
        var editor = await Json(HttpMethod.Get, "/api/sentences/1/fix", TestUsers.UserB);
        editor.GetProperty("original").GetProperty("text").GetString().Should().Be(Line);
        editor.GetProperty("shared").GetProperty("text").GetString().Should().Be("えっ、なに、その髪！");
        editor.GetProperty("cues").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task AUsersOwnFixWinsOverASharedOne()
    {
        await Save(1, Fix(english: "Hey, your hair!"), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);

        await Save(1, Fix(english: "What did you do to your hair?"));

        (await Json(HttpMethod.Get, "/api/sentence-translation/1")).GetProperty("translation").GetProperty("text").GetString()
            .Should().Be("What did you do to your hair?");
        (await Json(HttpMethod.Get, "/api/sentence-translation/1", TestUsers.UserB)).GetProperty("translation").GetProperty("text").GetString()
            .Should().Be("Hey, your hair!");
    }

    [Fact]
    public async Task TheOnlySentenceOnACardCantBeRemoved()
    {
        var mined = await Mine("頭", 1, 2);

        (await Send(HttpMethod.Delete, $"/api/mined/sentences/{mined[0]}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Delete, $"/api/mined/sentences/{mined[1]}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await InUserDb(db => db.MinedSentences.Select(s => s.MinedSentenceId).ToListAsync())).Should().Equal(mined[1]);
    }

    [Fact]
    public async Task AnotherUsersSentenceCantBeRemoved()
    {
        var mined = await Mine("頭", 1, 2);

        (await Send(HttpMethod.Delete, $"/api/mined/sentences/{mined[0]}", user: TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NotAProblemClosesEveryFlagOnTheLine()
    {
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "english" });
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "media" }, TestUsers.UserB);

        (await Send(HttpMethod.Post, "/api/admin/sentence-flags/1/reject", null, TestUsers.Admin)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await InUserDb(db => db.SentenceFlags.Select(f => f.Resolution).ToListAsync())).Should().AllBe(SentenceFlagResolution.Rejected);
    }

    [Fact]
    public async Task SubmittingMovesTheMarkedWordOnEveryonesMinedSentences()
    {
        var mined = await Mine("頭", 1);
        await Save(1, Fix(text: "ええっ、なに、その頭！"), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);

        var sentence = await InUserDb(db => db.MinedSentences.SingleAsync(s => s.MinedSentenceId == mined[0]));
        (sentence.TargetPosition, sentence.TargetLength).Should().Be((9, 1));
        var rebuild = (await Json(HttpMethod.Get, "/api/mined/rebuild")).EnumerateArray().Single().GetProperty("sentence");
        (rebuild.GetProperty("text").GetString(), rebuild.GetProperty("wordPosition").GetInt32()).Should().Be(("ええっ、なに、その頭！", 9));
    }

    [Fact]
    public async Task ASubmitTheParserFailsOnChangesNothing()
    {
        await Save(1, Fix(text: StubSentenceTextParser.Fails), TestUsers.Admin);

        (await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await SentenceText(1, TestUsers.UserB)).Should().Be(Line);
        (await InUserDb(db => db.SentenceFixes.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ANewSharedClipShowsItsOwnMiddleFrame()
    {
        await Save(1, Fix(pictureMs: 3000), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);
        await Save(1, Fix(startMs: 4300, endMs: 6250), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);

        using var scope = factory.Services.CreateScope();
        var line = await scope.ServiceProvider.GetRequiredService<JitenDbContext>().ExampleSentences.SingleAsync(s => s.SentenceId == 1);
        (line.StartMs, line.EndMs, line.PictureMs).Should().Be((4300, 6250, null));
    }

    [Fact]
    public async Task TheSentenceANoteWasMadeWithCantBeRemoved()
    {
        var mined = await Mine("頭", 1, 2);
        await InUserDb(async db =>
        {
            var note = await db.MinedNotes.SingleAsync();
            var own = await db.MinedSentences.SingleAsync(s => s.MinedSentenceId == mined[0]);
            own.SourceAnkiNoteId = note.AnkiNoteId;
            return await db.SaveChangesAsync();
        });

        (await Send(HttpMethod.Delete, $"/api/mined/sentences/{mined[0]}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Delete, $"/api/mined/sentences/{mined[1]}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ALineNoCueMatchesStillListsCuesToPick()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.ExampleSentences.Add(new ExampleSentence { SentenceId = 5, DeckId = 2, Position = 2, Text = "全然違う文", Tokens = NoWords, WordKeys = [] });
            await db.SaveChangesAsync();
        }

        var editor = await Json(HttpMethod.Get, "/api/sentences/5/fix");

        editor.GetProperty("original").GetProperty("startMs").ValueKind.Should().Be(JsonValueKind.Null);
        editor.GetProperty("cues").GetArrayLength().Should().Be(3);
        editor.GetProperty("cues").EnumerateArray().Should().OnlyContain(c => !c.GetProperty("inClip").GetBoolean());
    }

    /// <summary>Line 3 (a book, no subtitles) with a cached AI translation of its text and of another text.</summary>
    private async Task SeedBookTranslationsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Sentences.AddRange(new Sentence { Id = 20, TextHash = [2], Text = "本の行" }, new Sentence { Id = 21, TextHash = [3], Text = "本の髪" });
        db.ExampleSentenceTranslations.AddRange(
            new ExampleSentenceTranslation { CentralSentenceId = 20, Text = "A line of the book", Provider = "test", Model = "m", CreatedAt = DateTime.UtcNow },
            new ExampleSentenceTranslation { CentralSentenceId = 21, Text = "The book's hair", Provider = "test", Model = "m", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await db.ExampleSentences.Where(s => s.SentenceId == 3).ExecuteUpdateAsync(u => u.SetProperty(s => s.CentralSentenceId, 20L));
    }

    [Fact]
    public async Task TheAiEnglishOfAFixedTextIsTheFixedTexts()
    {
        await SeedBookTranslationsAsync();
        await Save(3, Fix(text: "本の髪"));

        (await Json(HttpMethod.Get, "/api/sentence-translation/3")).GetProperty("translation").GetProperty("text").GetString().Should().Be("The book's hair");
        (await Json(HttpMethod.Get, "/api/sentence-translation/3", TestUsers.UserB)).GetProperty("translation").GetProperty("text").GetString()
            .Should().Be("A line of the book");

        var batch = await Send(HttpMethod.Post, "/api/sentence-translation/batch", new { sentenceIds = new[] { 3L } });
        (await batch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("translations").GetProperty("3").GetProperty("text").GetString()
            .Should().Be("The book's hair");
    }

    [Fact]
    public async Task AFixedTextWithNoCachedEnglishShowsNoneRatherThanTheLines()
    {
        await SeedBookTranslationsAsync();
        await Save(3, Fix(text: "本の新しい行"));

        (await Json(HttpMethod.Get, "/api/sentence-translation/3")).GetProperty("translation").ValueKind.Should().Be(JsonValueKind.Null);
        var batch = await Send(HttpMethod.Post, "/api/sentence-translation/batch", new { sentenceIds = new[] { 3L } });
        (await batch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("translations").TryGetProperty("3", out _).Should().BeFalse();
    }

    private class FakeTranslator : ITranslationProvider
    {
        public string Id => "fake";
        public string Name => "Fake";
        public IReadOnlyList<SettingField> Fields => [];
        public Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["m"]);
        public Task<TranslationResult> TranslateAsync(ProviderConfig config, string prompt, CancellationToken ct) =>
            Task.FromResult(new TranslationResult(prompt.Contains("本の新しい行") ? "A new line of the book" : "A line of the book", "m", null, null));
        public ModelPrice? PriceFor(string model) => null;
    }

    [Fact]
    public async Task TheAiTranslatesTheCallersFixedTextAndCachesItUnderThatText()
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<TranslationSettingsStore>();
        var settings = await store.GetAsync();
        settings.ActiveProvider = "fake";
        await store.SaveAsync(settings);
        var translations = new SentenceTranslationService(services.GetRequiredService<JitenDbContext>(), services.GetRequiredService<SentenceCueLookup>(),
                                                          services.GetRequiredService<IMemoryCache>(), store, [new FakeTranslator()],
                                                          NullLogger<SentenceTranslationService>.Instance);
        try
        {
            (await translations.GetAsync(3, allowAi: true, CancellationToken.None, fixedText: "本の新しい行"))!.Text.Should().Be("A new line of the book");

            var db = services.GetRequiredService<JitenDbContext>();
            var row = await db.Sentences.SingleAsync(s => s.Text == "本の新しい行");
            (await db.ExampleSentenceTranslations.SingleAsync(t => t.CentralSentenceId == row.Id)).Text.Should().Be("A new line of the book");
            (await db.ExampleSentences.SingleAsync(s => s.SentenceId == 3)).CentralSentenceId.Should().NotBe(row.Id);

            // Cached now, and only for that text
            (await translations.GetAsync(3, allowAi: false, CancellationToken.None, fixedText: "本の新しい行"))!.Text.Should().Be("A new line of the book");
            (await translations.GetAsync(3, allowAi: false, CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            settings.ActiveProvider = "";
            await store.SaveAsync(settings);
        }
    }

    [Fact]
    public async Task SubmittingATextDeletesTheOldTextsRowOnceNoLineHasIt()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.ExampleSentenceTranslations.Add(new ExampleSentenceTranslation { CentralSentenceId = 10, Text = "Huh?", Provider = "test", Model = "m", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await Save(1, Fix(text: "えっ、なに、その髪！"), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);

        // Line 2 still has the old text
        using (var scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<JitenDbContext>().Sentences.AnyAsync(s => s.Id == 10)).Should().BeTrue();

        await Save(2, Fix(text: "えっ、なに、その髪！"), TestUsers.Admin);
        await Send(HttpMethod.Post, "/api/sentences/2/fix/submit", new { }, TestUsers.Admin);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            (await db.Sentences.AnyAsync(s => s.Id == 10)).Should().BeFalse();
            (await db.ExampleSentenceTranslations.AnyAsync(t => t.CentralSentenceId == 10)).Should().BeFalse();
            var lines = await db.ExampleSentences.Where(s => s.SentenceId == 1 || s.SentenceId == 2).ToListAsync();
            lines.Select(l => l.CentralSentenceId).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SubmittingWhileTheTitlesReparseSavesIsRefused()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.Add(new Deck { DeckId = 905, OriginalTitle = "Saving", MediaType = MediaType.Anime });
            db.ExampleSentences.Add(new ExampleSentence { SentenceId = 905, DeckId = 905, Position = 1, Text = "保存中の行", Tokens = NoWords, WordKeys = [] });
            await db.SaveChangesAsync();
        }
        await Save(905, Fix(text: "保存中の文"), TestUsers.Admin);

        Jiten.Api.Jobs.ReparseSaves.Enter([905]);
        try
        {
            var refused = await Send(HttpMethod.Post, "/api/sentences/905/fix/submit", new { }, TestUsers.Admin);
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await SentenceText(905, TestUsers.UserB)).Should().Be("保存中の行");
        }
        finally
        {
            Jiten.Api.Jobs.ReparseSaves.Exit([905]);
        }

        (await Send(HttpMethod.Post, "/api/sentences/905/fix/submit", new { }, TestUsers.Admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SentenceText(905, TestUsers.UserB)).Should().Be("保存中の文");
    }
}
