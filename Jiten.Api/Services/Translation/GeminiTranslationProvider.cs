using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jiten.Api.Services.Translation;

/// <summary>Google's Gemini, through the Generative Language REST API (the API key from Google AI Studio).</summary>
public class GeminiTranslationProvider(IHttpClientFactory httpClientFactory) : ITranslationProvider
{
    public const string DefaultModel = "gemini-flash-latest";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    public string Id => "gemini";
    public string Name => "Gemini (Google)";

    public IReadOnlyList<SettingField> Fields { get; } =
    [
        new("apiKey", "API key", SettingFieldKind.Secret, Help: "From aistudio.google.com → Get API key. The free tier is enough for mining."),
        new("model", "Model", SettingFieldKind.Model, DefaultModel),
    ];

    /// <summary>Gemini prices change often and depend on tier; the estimate just says "unknown" for them.</summary>
    public ModelPrice? PriceFor(string model) => null;

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        var json = await Send(config, HttpMethod.Get, "models?pageSize=1000", null, ct);
        return json["models"]?.AsArray()
                   .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.GetValue<string>() == "generateContent") == true)
                   .Select(m => m!["name"]!.GetValue<string>().Replace("models/", ""))
                   .ToList() ?? [];
    }

    public async Task<TranslationResult> TranslateAsync(ProviderConfig config, string prompt, CancellationToken ct)
    {
        var model = config.Get("model");
        var body = new { contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } } };
        var json = await Send(config, HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:generateContent", body, ct);

        var candidate = json["candidates"]?.AsArray().FirstOrDefault();
        var text = string.Concat(candidate?["content"]?["parts"]?.AsArray()
                                         .Where(p => p?["thought"]?.GetValue<bool>() != true)
                                         .Select(p => p?["text"]?.GetValue<string>() ?? "") ?? []).Trim();
        if (text.Length == 0)
        {
            var reason = candidate?["finishReason"]?.GetValue<string>() ?? json["promptFeedback"]?["blockReason"]?.GetValue<string>() ?? "no candidates";
            throw new TranslationProviderException($"Gemini returned no text ({reason}).");
        }

        var usage = json["usageMetadata"];
        return new TranslationResult(text, json["modelVersion"]?.GetValue<string>() ?? model,
                                     usage?["promptTokenCount"]?.GetValue<int>(),
                                     (usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0) + (usage?["thoughtsTokenCount"]?.GetValue<int>() ?? 0));
    }

    private async Task<JsonNode> Send(ProviderConfig config, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("x-goog-api-key", config.Get("apiKey"));
        if (body != null) request.Content = JsonContent.Create(body);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryParse(content)?["error"]?["message"]?.GetValue<string>() ?? content;
            throw new TranslationProviderException($"Gemini: {(int)response.StatusCode} {message}");
        }

        return TryParse(content) ?? throw new TranslationProviderException("Gemini returned an unreadable response.");
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
