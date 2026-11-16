namespace Jiten.Api.Services.Translation;

/// <summary>
/// One line to translate. LLM providers send <see cref="Prompt"/>, which already holds the sentence and its context;
/// providers that take text directly, like DeepL, use the parts.
/// </summary>
/// <param name="Before">Japanese lines before the sentence, oldest first.</param>
/// <param name="After">Japanese lines after the sentence.</param>
public record TranslationRequest(string Prompt, string Sentence, IReadOnlyList<string> Before, IReadOnlyList<string> After);

public record TranslationResult(string Text, string Model, int? InputTokens, int? OutputTokens);

/// <summary>US dollars per million tokens.</summary>
public record ModelPrice(decimal Input, decimal Output);

/// <summary>
/// An AI service that translates a prompt. To add one (Groq, OpenRouter, Ollama, DeepL...), implement this and register it
/// in Program.cs; the admin page draws its settings form from <see cref="Fields"/>.
/// </summary>
public interface ITranslationProvider
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<SettingField> Fields { get; }

    Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken ct);

    Task<TranslationResult> TranslateAsync(ProviderConfig config, TranslationRequest request, CancellationToken ct);

    /// <summary>Null when the provider doesn't know the model's price.</summary>
    ModelPrice? PriceFor(string model);
}

/// <summary>A provider refused or returned nothing usable; the message is shown to the admin.</summary>
public class TranslationProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The provider's quota for the billing period is used up: a bulk run stops rather than counting failures.</summary>
public class TranslationQuotaException(string message) : TranslationProviderException(message);
