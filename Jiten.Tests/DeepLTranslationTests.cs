using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.Translation;
using Jiten.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jiten.Tests;

public class DeepLTranslationTests
{
    [Fact]
    public void Combine_PutsTheSentenceInATagAfterTheContext_Escaped()
    {
        DeepLTranslationProvider.Combine(["大変だ！", "A&B<c>"], "行こう")
                                .Should().Be("大変だ！\nA&amp;B&lt;c&gt;\n<t>行こう</t>");
    }

    [Theory]
    [InlineData("This is bad!\n<t>Let's go.</t>", "Let's go.")]
    [InlineData("Context.\n<t> Tom &amp; Jerry </t>", "Tom & Jerry")]
    [InlineData("Context. <t>Line\none</t> tail", "Line\none")]
    [InlineData("Context. Let's go.", null)]
    [InlineData("Context. <t> </t>", null)]
    public void ExtractTarget_TakesTheTaggedText(string translated, string? expected)
    {
        DeepLTranslationProvider.ExtractTarget(translated).Should().Be(expected);
    }

    [Fact]
    public void SplitContext_SeparatesLinesBeforeOwnCuesAndAfter()
    {
        var cues = Enumerable.Range(0, 8).Select(i => new SubtitleCue(i * 1000, i * 1000 + 900, $"l{i}")).ToList();

        var (before, own, after) = SentenceTranslationService.SplitContext(new SentenceCues(1, "x", cues, 3, 4), 2);

        before.Should().Equal("l1", "l2");
        own.Should().Equal("l3", "l4");
        after.Should().Equal("l5", "l6");
        SentenceTranslationService.SplitContext(new SentenceCues(1, "x", cues, 0, 0), 3).Before.Should().BeEmpty();
        SentenceTranslationService.SplitContext(new SentenceCues(1, "x", cues, 7, 7), 3).After.Should().BeEmpty();
        var none = SentenceTranslationService.SplitContext(null, 3);
        none.Before.Concat(none.Own).Concat(none.After).Should().BeEmpty();
    }

    [Fact]
    public async Task Combined_SendsLinesBeforeInTheText_AndKeepsTheTaggedPart()
    {
        var (provider, sent) = Provider(_ => "Context line.\n<t>Let's go.</t>");

        var result = await provider.TranslateAsync(Config("k:fx"), Request("行こう", ["一", "二", "三"]), default);

        result.Text.Should().Be("Let's go.");
        result.Model.Should().Be("deepl");
        sent.Should().ContainSingle();
        sent[0].Url.Should().Be("https://api-free.deepl.com/v2/translate");
        sent[0].Auth.Should().Be("DeepL-Auth-Key k:fx");
        sent[0].Body["text"]![0]!.GetValue<string>().Should().Be("二\n三\n<t>行こう</t>");
        sent[0].Body["tag_handling"]!.GetValue<string>().Should().Be("xml");
        sent[0].Body["context"].Should().BeNull();
    }

    [Fact]
    public async Task Combined_TranslatesTheSentenceAloneWhenTheTagIsLost()
    {
        var (provider, sent) = Provider(body => body["tag_handling"] != null ? "Context line. Let's go." : "Let's go.");

        var result = await provider.TranslateAsync(Config("k"), Request("行こう", ["一"]), default);

        result.Text.Should().Be("Let's go.");
        sent.Should().HaveCount(2);
        sent[0].Url.Should().Be("https://api.deepl.com/v2/translate");
        sent[1].Body["text"]![0]!.GetValue<string>().Should().Be("行こう");
        sent[1].Body["tag_handling"].Should().BeNull();
    }

    [Fact]
    public async Task ContextParameter_SendsLinesBeforeAsContext()
    {
        var (provider, sent) = Provider(_ => "Let's go.");

        await provider.TranslateAsync(Config("k", "context parameter"), Request("行こう", ["一", "二"]), default);

        sent[0].Body["text"]![0]!.GetValue<string>().Should().Be("行こう");
        sent[0].Body["context"]!.GetValue<string>().Should().Be("一\n二");
        sent[0].Body["tag_handling"].Should().BeNull();
    }

    [Fact]
    public async Task NoLinesBefore_SendsTheSentenceAlone()
    {
        var (provider, sent) = Provider(_ => "Let's go.");

        await provider.TranslateAsync(Config("k", lines: "0"), Request("行こう", ["一"]), default);

        sent[0].Body["text"]![0]!.GetValue<string>().Should().Be("行こう");
        sent[0].Body["tag_handling"].Should().BeNull();
    }

    [Fact]
    public async Task QuotaExceeded_IsAQuotaError_SoABulkRunStops()
    {
        var (provider, _) = Provider(_ => "", (HttpStatusCode)456);

        var act = () => provider.TranslateAsync(Config("k"), Request("行こう", []), default);

        (await act.Should().ThrowAsync<TranslationQuotaException>()).WithMessage("*quota*");
    }

    private record Sent(string Url, string? Auth, JsonNode Body);

    private static TranslationRequest Request(string sentence, string[] before) => new("prompt", sentence, before, []);

    private static ProviderConfig Config(string key, string mode = "combined", string lines = "2")
    {
        var fields = new DeepLTranslationProvider(Mock.Of<IHttpClientFactory>(), NullLogger<DeepLTranslationProvider>.Instance).Fields;
        return new ProviderConfig(new Dictionary<string, string> { ["apiKey"] = key, ["contextMode"] = mode, ["linesBefore"] = lines }, fields);
    }

    private static (DeepLTranslationProvider, List<Sent>) Provider(Func<JsonNode, string> reply, HttpStatusCode status = HttpStatusCode.OK)
    {
        var sent = new List<Sent>();
        var handler = new StubHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            sent.Add(new Sent(request.RequestUri!.ToString(), request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null, body));
            var json = new JsonObject { ["translations"] = new JsonArray(new JsonObject { ["text"] = reply(body) }) }.ToJsonString();
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return (new DeepLTranslationProvider(factory.Object, NullLogger<DeepLTranslationProvider>.Instance), sent);
    }

    private class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
