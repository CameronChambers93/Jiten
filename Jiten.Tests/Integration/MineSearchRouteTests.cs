using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Saved Mine searches are stored per user and cleaned on save; the bulk-run plan route checks its input.</summary>
public class MineSearchRouteTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserSettings.RemoveRange(userDb.UserSettings);
        // The Mine routes carry the sentence-stats gate (Trial and up) until GDR-005 removes it.
        foreach (var user in await userDb.Users.Where(u => u.Id == TestUsers.UserA || u.Id == TestUsers.UserB).ToListAsync())
            user.AdminPremiumOverride = true;
        await userDb.SaveChangesAsync();
        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        jitenPlus.InvalidateTier(TestUsers.UserA);
        jitenPlus.InvalidateTier(TestUsers.UserB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string user = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(method, url).WithUser(user);
        if (body != null) request = request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task SearchesAreSavedPerUser_AndCleaned()
    {
        var empty = await Send(HttpMethod.Get, "/api/mining/searches");
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(0);

        var put = await Send(HttpMethod.Put, "/api/mining/searches", new object[]
        {
            new { name = "Bunny Drop", query = new Dictionary<string, string> { ["titles"] = "5", ["deck"] = "Core 2000", ["bogus"] = "1" },
                  run = new { pipelineId = "mine", perWord = 2, whenAsked = "new" } }
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await (await Send(HttpMethod.Get, "/api/mining/searches")).Content.ReadFromJsonAsync<JsonElement>();
        saved.GetArrayLength().Should().Be(1);
        var search = saved[0];
        search.GetProperty("name").GetString().Should().Be("Bunny Drop");
        search.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        search.GetProperty("query").TryGetProperty("bogus", out _).Should().BeFalse();
        search.GetProperty("query").GetProperty("deck").GetString().Should().Be("Core 2000");
        search.GetProperty("run").GetProperty("perWord").GetInt32().Should().Be(2);
        search.GetProperty("run").GetProperty("whenAsked").GetString().Should().Be("new");

        var other = await (await Send(HttpMethod.Get, "/api/mining/searches", user: TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        other.GetArrayLength().Should().Be(0, "searches belong to the user who saved them");

        using var scope = factory.Services.CreateScope();
        var json = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserSettings.AsNoTracking()
                              .Where(s => s.UserId == TestUsers.UserA).Select(s => s.MineSearchesJson).SingleAsync();
        json.Should().Contain("Bunny Drop");
    }

    [Fact]
    public async Task DuplicateNamesAreA400()
    {
        var response = await Send(HttpMethod.Put, "/api/mining/searches", new object[] { new { name = "Core" }, new { name = "CORE" } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Contain("called");
    }

    [Fact]
    public async Task PlanRefusesBadSentencesPerWord()
    {
        var response = await Send(HttpMethod.Post, "/api/mining/sentences/all", new { search = new { }, perWord = 6 });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlanOfAnEmptyLibraryIsEmpty()
    {
        var response = await Send(HttpMethod.Post, "/api/mining/sentences/all", new { search = new { }, perWord = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        plan.GetProperty("items").GetArrayLength().Should().Be(0);
        plan.GetProperty("capped").GetBoolean().Should().BeFalse();
    }
}
