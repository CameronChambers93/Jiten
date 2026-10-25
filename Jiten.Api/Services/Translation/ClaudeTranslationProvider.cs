using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Jiten.Api.Services.Translation;

/// <summary>Anthropic's Claude, through the official SDK.</summary>
public class ClaudeTranslationProvider : ITranslationProvider
{
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Thinking counts toward this; a one-line translation needs far less.</summary>
    private const int MaxTokens = 4000;

    private static readonly Dictionary<string, ModelPrice> Prices = new()
    {
        ["claude-fable-5-1"] = new(10m, 50m),
        ["claude-opus-5-5"] = new(4m, 20m),
        ["claude-opus-5"] = new(5m, 25m),
        ["claude-sonnet-5-5"] = new(2m, 10m),
        ["claude-sonnet-5"] = new(2m, 10m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
    };

    public string Id => "claude";
    public string Name => "Claude (Anthropic)";

    public IReadOnlyList<SettingField> Fields { get; } =
    [
        new("apiKey", "API key", SettingFieldKind.Secret, Help: "From console.anthropic.com → API keys. Billed separately from a Claude subscription."),
        new("model", "Model", SettingFieldKind.Model, DefaultModel),
        new("effort", "Effort", SettingFieldKind.Select, "low", ["low", "medium", "high"],
            "How much the model thinks before answering. Low is plenty for one line and costs least."),
    ];

    public ModelPrice? PriceFor(string model) => Prices.GetValueOrDefault(model);

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        var page = await Client(config).Models.List(cancellationToken: ct);
        return page.Items.Select(m => m.ID).ToList();
    }

    public async Task<TranslationResult> TranslateAsync(ProviderConfig config, string prompt, CancellationToken ct)
    {
        var model = config.Get("model");
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            // A refusal is re-served by the model's default fallback; models without fallbacks just run as requested.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = config.Get("effort") },
            Messages = [new BetaUserMessageParam { Content = prompt }],
        };

        BetaMessage response;
        try
        {
            response = await Client(config).Beta.Messages.Create(parameters, ct);
        }
        catch (AnthropicApiException e)
        {
            throw new TranslationProviderException($"Claude: {e.Message}", e);
        }

        if (response.StopReason == "refusal")
            throw new TranslationProviderException($"Claude declined to translate this line ({response.StopDetails?.Category}).");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
        if (text.Length == 0)
            throw new TranslationProviderException($"Claude returned no text (stop reason {response.StopReason}).");

        return new TranslationResult(text, response.Model, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);
    }

    private static AnthropicClient Client(ProviderConfig config) => new() { ApiKey = config.Get("apiKey") };
}
