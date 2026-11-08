using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>The sentence pipelines (n1miner): built from the card settings until saved, validated, and saved per user.</summary>
public class MiningPipelineTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.AnkiNoteTypeSetups.RemoveRange(userDb.AnkiNoteTypeSetups);
        userDb.UserSettings.RemoveRange(userDb.UserSettings);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static object Setup(string model, string existing, bool picture = true) => new
    {
        modelName = model, filter = (string?)null,
        fieldRoles = new Dictionary<string, string> { ["Expression"] = "word", ["Sentence"] = "sentence" },
        preset = (string?)null, knownWords = (string?)null, importSentences = false, takeFlags = false, foldDuplicates = false,
        updateMainFields = false, sentenceList = "none", scriptVersion = (int?)null,
        mining = new { existing, parts = new { audio = true, picture, english = true, video = true } }
    };

    private Task<HttpResponseMessage> Put(string url, object body, string user = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, url).WithUser(user).WithJsonContent(body));

    private async Task<JsonElement> Get(string url, string user = TestUsers.UserA)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithUser(user));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The property is left out or null: the API writes nulls, the stored JSON leaves them out.</summary>
    private static bool Absent(JsonElement e, string name) => !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

    private static string Kinds(JsonElement pipeline) =>
        string.Join(",", pipeline.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));

    [Fact]
    public async Task CardSettingsBecomeMineAndKeepForJiten()
    {
        (await Put("/api/anki-sync/setups", Setup("Core 2000", "auto"))).EnsureSuccessStatusCode();
        (await Put("/api/anki-sync/setups", Setup("Lapis+", "ask", picture: false))).EnsureSuccessStatusCode();
        (await Put("/api/anki-sync/card-target", new
        {
            targets = new object[] { new { deckName = "Jiten test", modelName = "Lapis+", tags = "jiten", when = (object?)null } },
            padStartMs = 100, padEndMs = 300
        })).EnsureSuccessStatusCode();

        var result = await Get("/api/mining/pipelines");
        result.GetProperty("saved").GetBoolean().Should().BeFalse();
        var pipelines = result.GetProperty("pipelines");
        pipelines.GetArrayLength().Should().Be(2);
        var mine = pipelines[0];
        mine.GetProperty("name").GetString().Should().Be("Mine");
        Kinds(mine).Should().Be("build,anki,anki,tags");

        var steps = mine.GetProperty("steps");
        steps[0].GetProperty("picture").GetBoolean().Should().BeFalse("the Build step takes the first target's note type's parts");
        steps[0].GetProperty("padStartMs").GetInt32().Should().Be(100);
        steps[0].GetProperty("english").GetString().Should().Be("subtitlesThenAi");
        steps[1].GetProperty("when").GetProperty("cardNoteTypes")[0].GetString().Should().Be("Core 2000");
        steps[1].GetProperty("hasCard").GetString().Should().Be("add");
        Absent(steps[2], "when").Should().BeTrue();
        steps[2].GetProperty("hasCard").GetString().Should().Be("ask");
        steps[2].GetProperty("targets")[0].GetProperty("deckName").GetString().Should().Be("Jiten test");
        steps[3].GetProperty("tags").GetString().Should().Be("jiten");

        pipelines[1].GetProperty("name").GetString().Should().Be("Keep for Jiten");
        pipelines[1].GetProperty("steps").GetArrayLength().Should().Be(0);
        result.GetProperty("defaults").GetProperty("deckSentences").GetString().Should().Be(mine.GetProperty("id").GetString());

        using var scope = factory.Services.CreateScope();
        var json = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserSettings.AsNoTracking()
                              .Where(s => s.UserId == TestUsers.UserA).Select(s => s.MiningPipelinesJson).SingleAsync();
        json.Should().Be("{}", "reading builds them without saving, so the card settings stay in charge until a save");
    }

    [Fact]
    public async Task SavedPipelinesComeBackAndDefaultsPointAtOneThatExists()
    {
        var body = new
        {
            pipelines = new object[]
            {
                new { id = "anime", name = " Anime, no screenshot ", steps = new object[]
                {
                    new { kind = "build", audio = true, picture = false, video = true, english = "subtitlesOnly", padStartMs = 400, padEndMs = 9999 },
                    new { kind = "anki", hasCard = "new", noCard = "targets", when = new { mediaTypes = new[] { 1 }, cardNoteTypes = Array.Empty<string>() },
                          targets = new object[] { new { deckName = "Anime", modelName = "Lapis+", when = new { mediaTypes = new[] { 1 } } } } },
                    new { kind = "tags", tags = "anime", titleTag = true, audio = true },
                } },
                new { id = "keep", name = "Keep for Jiten", steps = Array.Empty<object>() },
            },
            defaults = new { word = "anime", mine = "gone", deckSentences = "keep", sentence = "keep" },
        };
        var put = await Put("/api/mining/pipelines", body);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await Get("/api/mining/pipelines");
        result.GetProperty("saved").GetBoolean().Should().BeTrue();
        var anime = result.GetProperty("pipelines")[0];
        anime.GetProperty("name").GetString().Should().Be("Anime, no screenshot");
        var steps = anime.GetProperty("steps");
        steps[0].GetProperty("padEndMs").GetInt32().Should().Be(5000);
        steps[0].GetProperty("english").GetString().Should().Be("subtitlesOnly");
        steps[1].GetProperty("when").GetProperty("mediaTypes")[0].GetInt32().Should().Be(1);
        Absent(steps[1].GetProperty("targets")[0], "when").Should().BeTrue("the last target takes everything else");
        Absent(steps[2], "audio").Should().BeTrue("a Tags step keeps only its own options");
        steps[2].GetProperty("titleTag").GetBoolean().Should().BeTrue();

        var defaults = result.GetProperty("defaults");
        defaults.GetProperty("word").GetString().Should().Be("anime");
        defaults.GetProperty("mine").GetString().Should().Be("anime", "a default naming no pipeline falls back to the first");
        defaults.GetProperty("deckSentences").GetString().Should().Be("keep");

        (await Get("/api/mining/pipelines", TestUsers.UserB)).GetProperty("saved").GetBoolean().Should().BeFalse("pipelines are per user");
    }

    [Theory]
    [InlineData("""{"pipelines":[],"defaults":null}""", "Keep at least one pipeline.")]
    [InlineData("""{"pipelines":[{"id":"a","name":"A","steps":[{"kind":"anki","hasCard":"ask","noCard":"skip","targets":[]}]}]}""", "A, step 1: add a target for new cards.")]
    [InlineData("""{"pipelines":[{"id":"a","name":"A","steps":[{"kind":"anki","noCard":"targets","targets":[{"deckName":"D","modelName":""}]}]}]}""", "A, step 1: target 1 needs a deck and a note type.")]
    [InlineData("""{"pipelines":[{"id":"a","name":"A","steps":[{"kind":"anki","noCard":"targets","targets":[{"deckName":"D","modelName":"M"},{"deckName":"D","modelName":"M"}]}]}]}""", "A, step 1: target 1 needs a media type or a title; only the last one takes everything else.")]
    [InlineData("""{"pipelines":[{"id":"a","name":"A","steps":[{"kind":"jiten"}]}]}""", "A, step 1: unknown kind of step.")]
    [InlineData("""{"pipelines":[{"id":"a","name":"A","steps":[{"kind":"anki"}]}]}""", "A, step 1: add a target for new cards.")]
    [InlineData("""{"pipelines":[{"id":"a","name":" ","steps":[]}]}""", "Every pipeline needs a name.")]
    public async Task InvalidPipelinesAreRefused(string json, string message)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/mining/pipelines")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }.WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Be(message);
    }

    [Fact]
    public async Task ASkipAnkiStepNeedsNoTargets()
    {
        var put = await Put("/api/mining/pipelines", new
        {
            pipelines = new object[] { new { id = "a", name = "Only Core 2000", steps = new object[] { new { kind = "anki", hasCard = "add", noCard = "skip" } } } },
            defaults = new { word = "a", mine = "a", deckSentences = "a", sentence = "a" },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
