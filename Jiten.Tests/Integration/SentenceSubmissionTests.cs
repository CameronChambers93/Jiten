using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Submitting fixes for review (sentence flags step 4): a user's submission waits, an admin approves or rejects it, and
/// users with their own fix of the line follow their setting.
/// </summary>
public class SentenceSubmissionTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly byte[] NoWords = ExampleSentenceTokens.Encode([]);
    private const string Line = "えっ、なに、その頭！";
    private const string Fixed = "えっ、なに、その髪！";

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.SentenceFixes.RemoveRange(userDb.SentenceFixes);
        userDb.SentenceFlags.RemoveRange(userDb.SentenceFlags);
        foreach (var settings in userDb.UserSettings) settings.ApprovedFixMode = null;
        await userDb.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.SentenceSubmissions.ExecuteDeleteAsync();
        await db.SentenceCorrections.ExecuteDeleteAsync();
        await db.ExampleSentences.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.Sentences.ExecuteDeleteAsync();
        db.Decks.AddRange(new Deck { DeckId = 1, OriginalTitle = "Osomatsu-san", MediaType = MediaType.Anime },
                          new Deck { DeckId = 2, OriginalTitle = "Episode 14", MediaType = MediaType.Anime, ParentDeckId = 1 },
                          new Deck { DeckId = 3, OriginalTitle = "Episode 3", MediaType = MediaType.Anime, ParentDeckId = 1 });
        db.DeckVideoSources.Add(new DeckVideoSource { DeckId = 2, VideoPath = "/media/ep14.mkv", SubtitleOffsetMs = 0 });
        db.Sentences.Add(new Sentence { Id = 10, TextHash = [1], Text = Line });
        db.ExampleSentences.AddRange(
            new ExampleSentence { SentenceId = 1, DeckId = 2, Position = 1, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
            new ExampleSentence { SentenceId = 2, DeckId = 3, Position = 9, Text = Line, Tokens = NoWords, WordKeys = [], CentralSentenceId = 10 },
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

    private async Task<JsonElement> Json(HttpMethod method, string url, object? body = null, string user = TestUsers.UserA)
    {
        var response = await Send(method, url, body, user);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> SentenceText(long id, string user) =>
        (await Json(HttpMethod.Get, $"/api/sentences/{id}", user: user)).GetProperty("sentence").GetProperty("text").GetString();

    private async Task<string?> English(long id, string user) =>
        (await Json(HttpMethod.Get, $"/api/sentence-translation/{id}", user: user)).GetProperty("translation").GetProperty("text").GetString();

    private async Task<T> InUserDb<T>(Func<UserDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<UserDbContext>());
    }

    private async Task<T> InDb<T>(Func<JitenDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    private async Task<long> Submit(long id, object fix, string user = TestUsers.UserA, long[]? sameTextIds = null)
    {
        await Save(id, fix, user);
        (await Json(HttpMethod.Post, $"/api/sentences/{id}/fix/submit", new { sameTextIds = sameTextIds ?? [] }, user))
            .GetProperty("applied").GetBoolean().Should().BeFalse();
        return await InDb(db => db.SentenceSubmissions.Where(s => s.UserId == user && s.ExampleSentenceId == id && s.Status == SentenceSubmissionStatus.Pending)
                                  .Select(s => s.SentenceSubmissionId).SingleAsync());
    }

    private Task<HttpResponseMessage> Approve(long submissionId) =>
        Send(HttpMethod.Post, $"/api/admin/sentence-submissions/{submissionId}/approve", null, TestUsers.Admin);

    private Task<List<Notification>> NotificationsFor(string user) => InDb(db => db.Notifications.Where(n => n.UserId == user).ToListAsync());

    [Fact]
    public async Task AUsersSubmissionWaitsAndChangesNothingForOthers()
    {
        await Submit(1, Fix(text: Fixed));

        (await SentenceText(1, TestUsers.UserB)).Should().Be(Line);
        (await SentenceText(1, TestUsers.UserA)).Should().Be(Fixed);
        (await Json(HttpMethod.Get, "/api/sentences/1/fix")).GetProperty("submission").GetProperty("status").GetString().Should().Be("pending");

        var queue = await Json(HttpMethod.Get, "/api/admin/sentence-submissions", user: TestUsers.Admin);
        queue.GetProperty("totalCount").GetInt32().Should().Be(1);
        var item = queue.GetProperty("items")[0];
        item.GetProperty("now").GetProperty("text").GetString().Should().Be(Line);
        item.GetProperty("now").GetProperty("english").GetString().Should().Be("Huh? What's with your hair?");
        item.GetProperty("proposed").GetProperty("text").GetString().Should().Be(Fixed);
        item.GetProperty("proposed").GetProperty("english").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("changed").GetBoolean().Should().BeFalse();
        (await Json(HttpMethod.Get, "/api/admin/sentence-submissions/count", user: TestUsers.Admin)).GetProperty("pending").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SubmittingAgainReplacesThePendingOneAndWithdrawRemovesIt()
    {
        await Submit(1, Fix(english: "Your hair!"));
        await Submit(1, Fix(english: "What's with your hair?!"));

        var submissions = await InDb(db => db.SentenceSubmissions.ToListAsync());
        submissions.Should().ContainSingle().Which.English.Should().Be("What's with your hair?!");

        (await Send(HttpMethod.Delete, "/api/sentences/1/fix/submission")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await InDb(db => db.SentenceSubmissions.SingleAsync())).Status.Should().Be(SentenceSubmissionStatus.Withdrawn);
        (await Json(HttpMethod.Get, "/api/admin/sentence-submissions", user: TestUsers.Admin)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await InUserDb(db => db.SentenceFixes.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task ApprovingMakesItEveryonesAndTellsTheSubmitterAndFlaggers()
    {
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "japanese" }, TestUsers.UserB);
        var id = await Submit(1, Fix(text: Fixed, english: "Hey, your hair!"), sameTextIds: [2]);

        (await Approve(id)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SentenceText(1, TestUsers.UserB)).Should().Be(Fixed);
        (await SentenceText(2, TestUsers.UserB)).Should().Be(Fixed);
        (await English(1, TestUsers.UserB)).Should().Be("Hey, your hair!");
        (await InDb(db => db.SentenceCorrections.Select(c => c.UserId).ToListAsync())).Should().Equal(TestUsers.UserA, TestUsers.UserA);
        (await InDb(db => db.SentenceSubmissions.SingleAsync())).Should().BeEquivalentTo(
            new { Status = SentenceSubmissionStatus.Approved, ReviewerId = TestUsers.Admin }, o => o.ExcludingMissingMembers());

        // The submitter's fix is now the shared line, so theirs is gone; the flag closed
        (await InUserDb(db => db.SentenceFixes.CountAsync())).Should().Be(0);
        (await InUserDb(db => db.SentenceFlags.SingleAsync())).Resolution.Should().Be(SentenceFlagResolution.Fixed);
        (await NotificationsFor(TestUsers.UserA)).Select(n => n.Title).Should().Equal("Your fix was approved");
        (await NotificationsFor(TestUsers.UserB)).Select(n => (n.Title, n.LinkUrl)).Should().Equal(("A line you flagged was fixed", "/sentences/1"));
        (await NotificationsFor(TestUsers.Admin)).Should().BeEmpty();
        (await Json(HttpMethod.Get, "/api/sentences/1/fix")).GetProperty("submission").GetProperty("status").GetString().Should().Be("approved");
    }

    [Fact]
    public async Task RejectingKeepsTheFixAndSendsTheReason()
    {
        var id = await Submit(1, Fix(text: Fixed));

        (await Send(HttpMethod.Post, $"/api/admin/sentence-submissions/{id}/reject", new { note = "The audio says 頭." }, TestUsers.Admin))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SentenceText(1, TestUsers.UserB)).Should().Be(Line);
        (await SentenceText(1, TestUsers.UserA)).Should().Be(Fixed);
        var submission = (await Json(HttpMethod.Get, "/api/sentences/1/fix")).GetProperty("submission");
        (submission.GetProperty("status").GetString(), submission.GetProperty("reviewNote").GetString()).Should().Be(("rejected", "The audio says 頭."));
        (await NotificationsFor(TestUsers.UserA)).Single().Message.Should().Contain("The audio says 頭.");
        (await Approve(id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OthersOwnFixesFollowTheirSetting()
    {
        await Save(1, Fix(english: "B's English"), TestUsers.UserB);
        (await Send(HttpMethod.Put, "/api/sentence-flags/settings", new { approvedFixMode = "ask" }, TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Save(1, Fix(english: "The admin's English"), TestUsers.Admin);
        var id = await Submit(1, Fix(english: "Hey, your hair!"));

        await Approve(id);

        // Ask: B is asked; keep (the default): the admin's stays, not asked
        var approved = (await Json(HttpMethod.Get, "/api/sentence-flags", user: TestUsers.UserB)).GetProperty("approved");
        approved.GetArrayLength().Should().Be(1);
        (approved[0].GetProperty("mine").GetProperty("english").GetString(), approved[0].GetProperty("approved").GetProperty("english").GetString())
            .Should().Be(("B's English", "Hey, your hair!"));
        (await English(1, TestUsers.UserB)).Should().Be("B's English");
        (await English(1, TestUsers.Admin)).Should().Be("The admin's English");
        (await Json(HttpMethod.Get, "/api/sentence-flags", user: TestUsers.Admin)).GetProperty("approved").GetArrayLength().Should().Be(0);

        (await Send(HttpMethod.Post, "/api/sentences/1/fix/approved", new { use = true }, TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await English(1, TestUsers.UserB)).Should().Be("Hey, your hair!");
        (await Json(HttpMethod.Get, "/api/sentence-flags", user: TestUsers.UserB)).GetProperty("approved").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UseTheApprovedOneDropsTheCoveredPartsAtOnce()
    {
        await Save(1, Fix(text: "B's text", english: "B's English"), TestUsers.UserB);
        await Send(HttpMethod.Put, "/api/sentence-flags/settings", new { approvedFixMode = "use" }, TestUsers.UserB);

        await Approve(await Submit(1, Fix(english: "Hey, your hair!")));

        (await English(1, TestUsers.UserB)).Should().Be("Hey, your hair!");
        (await SentenceText(1, TestUsers.UserB)).Should().Be("B's text");
    }

    [Fact]
    public async Task TheSubmittersLaterEditsStayTheirs()
    {
        var id = await Submit(1, Fix(english: "Hey, your hair!"));
        await Save(1, Fix(english: "Hey, what happened to your hair?"));

        await Approve(id);

        (await English(1, TestUsers.UserB)).Should().Be("Hey, your hair!");
        (await English(1, TestUsers.UserA)).Should().Be("Hey, what happened to your hair?");
        (await Json(HttpMethod.Get, "/api/sentence-flags")).GetProperty("approved").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ASubmissionOfALineFixedSinceIsMarked()
    {
        await Submit(1, Fix(english: "Hey, your hair!"));
        await Save(1, Fix(english: "Huh? Your hair!"), TestUsers.Admin);
        await Json(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);

        var item = (await Json(HttpMethod.Get, "/api/admin/sentence-submissions", user: TestUsers.Admin)).GetProperty("items")[0];
        item.GetProperty("changed").GetBoolean().Should().BeTrue();
        item.GetProperty("now").GetProperty("english").GetString().Should().Be("Huh? Your hair!");
    }

    [Fact]
    public async Task OnlyAdminsReviewAndPrivateLinesCantBeSubmitted()
    {
        var id = await Submit(1, Fix(text: Fixed));
        (await Send(HttpMethod.Get, "/api/admin/sentence-submissions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Post, $"/api/admin/sentence-submissions/{id}/approve")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await Save(4, Fix(text: "ゲームの台詞"), TestUsers.UserB);
        (await Send(HttpMethod.Post, "/api/sentences/4/fix/submit", new { }, TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, "/api/sentences/2/fix/submit", new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheSettingDefaultsToKeepAndRefusesOthers()
    {
        (await Json(HttpMethod.Get, "/api/sentence-flags/settings")).GetProperty("approvedFixMode").GetString().Should().Be("keep");
        (await Send(HttpMethod.Put, "/api/sentence-flags/settings", new { approvedFixMode = "always" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NotAProblemTellsFlaggersButNotTheAdmin()
    {
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "english" }, TestUsers.UserB);
        await Send(HttpMethod.Post, "/api/sentence-flags", new { sentenceId = 1, reason = "english" }, TestUsers.Admin);

        await Send(HttpMethod.Post, "/api/admin/sentence-flags/1/reject", null, TestUsers.Admin);

        (await NotificationsFor(TestUsers.UserB)).Select(n => n.Title).Should().Equal("A line you flagged isn't being changed");
        (await NotificationsFor(TestUsers.Admin)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASubmissionIsApprovedOnce()
    {
        var id = await Submit(1, Fix(text: Fixed));

        (await Approve(id)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Approve(id)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await InDb(db => db.SentenceCorrections.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task AnApprovalTheParserFailsOnWaitsAgain()
    {
        var id = await Submit(1, Fix(text: StubSentenceTextParser.Fails));

        (await Approve(id)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await InDb(db => db.SentenceSubmissions.SingleAsync())).Status.Should().Be(SentenceSubmissionStatus.Pending);
        (await SentenceText(1, TestUsers.UserB)).Should().Be(Line);
    }

    [Fact]
    public async Task ASubmissionWhoseLineIsGoneIsntWaiting()
    {
        await Submit(1, Fix(english: "Hey, your hair!"));
        await InDb(db => db.ExampleSentences.IgnoreQueryFilters().Where(s => s.SentenceId == 1).ExecuteDeleteAsync());

        (await Json(HttpMethod.Get, "/api/admin/sentence-submissions", user: TestUsers.Admin)).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await Json(HttpMethod.Get, "/api/admin/sentence-submissions/count", user: TestUsers.Admin)).GetProperty("pending").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AskMeIsAboutTheApprovedPartsOnly()
    {
        // An older shared English that B kept; then a text-only approval
        await Save(1, Fix(english: "Old shared English"), TestUsers.Admin);
        await Json(HttpMethod.Post, "/api/sentences/1/fix/submit", new { }, TestUsers.Admin);
        await Save(1, Fix(text: "B's text", english: "B's English"), TestUsers.UserB);
        await Send(HttpMethod.Put, "/api/sentence-flags/settings", new { approvedFixMode = "ask" }, TestUsers.UserB);

        await Approve(await Submit(1, Fix(text: Fixed)));

        var item = (await Json(HttpMethod.Get, "/api/sentence-flags", user: TestUsers.UserB)).GetProperty("approved")[0];
        (item.GetProperty("mine").GetProperty("text").GetString(), item.GetProperty("approved").GetProperty("text").GetString()).Should().Be(("B's text", Fixed));
        item.GetProperty("mine").GetProperty("english").ValueKind.Should().Be(JsonValueKind.Null);

        await Send(HttpMethod.Post, "/api/sentences/1/fix/approved", new { use = true }, TestUsers.UserB);
        (await SentenceText(1, TestUsers.UserB)).Should().Be(Fixed);
        (await English(1, TestUsers.UserB)).Should().Be("B's English");
    }

    [Fact]
    public async Task EditingTheFixAnswersTheQuestion()
    {
        await Save(1, Fix(english: "B's English"), TestUsers.UserB);
        await Send(HttpMethod.Put, "/api/sentence-flags/settings", new { approvedFixMode = "ask" }, TestUsers.UserB);
        await Approve(await Submit(1, Fix(english: "Hey, your hair!")));

        await Save(1, Fix(english: "B's better English"), TestUsers.UserB);

        (await Json(HttpMethod.Get, "/api/sentence-flags", user: TestUsers.UserB)).GetProperty("approved").GetArrayLength().Should().Be(0);
    }
}
