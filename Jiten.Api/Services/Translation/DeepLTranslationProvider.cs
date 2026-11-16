using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jiten.Api.Services.Translation;

/// <summary>
/// DeepL's translation API. It translates text, not prompts, so it uses the request's sentence and the lines before it.
/// DeepL Free ignores the <c>context</c> parameter, so by default the context lines are sent in the text itself, with the
/// sentence marked by a tag, and only the tagged part of the translation is kept.
/// </summary>
public partial class DeepLTranslationProvider(IHttpClientFactory httpClientFactory, ILogger<DeepLTranslationProvider> logger) : ITranslationProvider
{
    public const string Model = "deepl";
    public const string CombinedMode = "combined";
    public const string ContextParameterMode = "context parameter";
    private const int DefaultLinesBefore = 2;

    private const string FreeUrl = "https://api-free.deepl.com/v2/translate";
    private const string ProUrl = "https://api.deepl.com/v2/translate";

    public string Id => "deepl";
    public string Name => "DeepL";

    public IReadOnlyList<SettingField> Fields { get; } =
    [
        new("apiKey", "API key", SettingFieldKind.Secret,
            Help: "From deepl.com → Account → API keys. Keys ending in :fx are API Free (500,000 characters a month). The prompt and Context lines below don't apply to DeepL."),
        new("contextMode", "Context mode", SettingFieldKind.Select, CombinedMode, [CombinedMode, ContextParameterMode],
            "Combined: the lines before are sent with the sentence and billed, and work on every plan. Context parameter: they aren't billed, but DeepL Free may ignore them."),
        new("linesBefore", "Lines before", SettingFieldKind.Number, DefaultLinesBefore.ToString(), Help: "Japanese lines before the sentence sent as context (0-10)."),
    ];

    /// <summary>DeepL bills per character, not per token, so the estimate shows "unknown".</summary>
    public ModelPrice? PriceFor(string model) => null;

    public Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([Model]);

    public async Task<TranslationResult> TranslateAsync(ProviderConfig config, TranslationRequest request, CancellationToken ct)
    {
        var lines = int.TryParse(config.Get("linesBefore"), out var n) ? Math.Clamp(n, 0, 10) : DefaultLinesBefore;
        var before = request.Before.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(lines).ToList();

        string text;
        if (before.Count == 0)
            text = await Translate(config, request.Sentence, null, false, ct);
        else if (config.Get("contextMode") == ContextParameterMode)
            text = await Translate(config, request.Sentence, string.Join("\n", before), false, ct);
        else if (ExtractTarget(await Translate(config, Combine(before, request.Sentence), null, true, ct)) is { } target)
            text = target;
        else
        {
            // Billed a second time; logged so a pattern of lost tags shows up.
            logger.LogWarning("DeepL lost the <t> tag around {Sentence}; translating it alone", request.Sentence);
            text = await Translate(config, request.Sentence, null, false, ct);
        }

        if (text.Length == 0) throw new TranslationProviderException("DeepL returned no text.");
        return new TranslationResult(text, Model, null, null);
    }

    /// <summary>The lines before, one per line, then the sentence inside <c>&lt;t&gt;</c>; all XML-escaped.</summary>
    public static string Combine(IReadOnlyList<string> before, string sentence) =>
        string.Join("\n", before.Select(Escape)) + "\n<t>" + Escape(sentence) + "</t>";

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The unescaped text inside <c>&lt;t&gt;</c>, or null when DeepL lost the tag or left it empty.</summary>
    public static string? ExtractTarget(string translated)
    {
        var match = TargetTag().Match(translated);
        if (!match.Success) return null;
        var text = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"<t>(.*?)</t>", RegexOptions.Singleline)]
    private static partial Regex TargetTag();

    private async Task<string> Translate(ProviderConfig config, string text, string? context, bool xml, CancellationToken ct)
    {
        var key = config.Get("apiKey");
        var body = new Dictionary<string, object>
        {
            ["text"] = new[] { text },
            ["source_lang"] = "JA",
            ["target_lang"] = "EN-US",
        };
        if (context != null) body["context"] = context;
        if (xml) body["tag_handling"] = "xml";

        using var request = new HttpRequestMessage(HttpMethod.Post, key.EndsWith(":fx") ? FreeUrl : ProUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {key}");
        request.Content = JsonContent.Create(body);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = (int)response.StatusCode switch
            {
                403 => "the API key was rejected",
                456 => "the character quota for this billing period is used up",
                429 => "too many requests; try again shortly",
                _ => TryParse(content)?["message"]?.GetValue<string>() ?? content
            };
            if ((int)response.StatusCode == 456) throw new TranslationQuotaException($"DeepL: 456 {message}");
            throw new TranslationProviderException($"DeepL: {(int)response.StatusCode} {message}");
        }

        var translated = TryParse(content)?["translations"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>()
                         ?? throw new TranslationProviderException("DeepL returned an unreadable response.");
        return translated.Trim();
    }

    private static JsonNode? TryParse(string content)
    {
        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
