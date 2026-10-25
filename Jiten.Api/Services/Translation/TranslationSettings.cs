using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.Translation;

public enum SettingFieldKind
{
    Text,

    /// <summary>Stored encrypted; the admin page only learns whether it is set and its last characters.</summary>
    Secret,

    Select,

    /// <summary>A model name, offered from the provider's model list.</summary>
    Model,

    Number
}

/// <summary>One setting a translation provider or subtitle source declares; the admin page draws its form from these.</summary>
/// <param name="ShowHint">For secrets, whether the admin page may show the last characters (fine for API keys, not passwords).</param>
public record SettingField(string Key, string Label, SettingFieldKind Kind, string? Default = null, string[]? Options = null, string? Help = null,
                           bool ShowHint = true);

/// <summary>A provider's or source's saved values, secrets already decrypted.</summary>
public class ProviderConfig(IReadOnlyDictionary<string, string> values, IReadOnlyList<SettingField> fields)
{
    public string Get(string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : fields.FirstOrDefault(f => f.Key == key)?.Default ?? "";

    /// <summary>True when every secret field has a value (API keys and the like).</summary>
    public bool HasSecrets => fields.Where(f => f.Kind == SettingFieldKind.Secret).All(f => Get(f.Key).Length > 0);
}

public static class CardTranslationMode
{
    public const string SubtitlesThenAi = "subtitlesThenAi";
    public const string SubtitlesOnly = "subtitlesOnly";
    public const string Off = "off";
    public static readonly string[] All = [SubtitlesThenAi, SubtitlesOnly, Off];
}

public static class PageTranslationMode
{
    public const string Off = "off";
    public const string Button = "button";
    public const string Auto = "auto";
    public static readonly string[] All = [Off, Button, Auto];
}

/// <summary>How English translations are found and shown; edited on the admin translation page.</summary>
public class TranslationSettings
{
    public string CardMode { get; set; } = CardTranslationMode.SubtitlesThenAi;
    public string PageMode { get; set; } = PageTranslationMode.Button;

    /// <summary>With <see cref="PageTranslationMode.Auto"/>, also call the AI for sentences without subtitle English.</summary>
    public bool PageAutoAi { get; set; }

    /// <summary>Id of the AI provider used for fallback translations; empty for none.</summary>
    public string ActiveProvider { get; set; } = "";

    /// <summary>Japanese lines sent before and after the sentence as context.</summary>
    public int ContextLines { get; set; } = 3;

    /// <summary>Custom prompt template; null uses <see cref="TranslationPrompt.Default"/>.</summary>
    public string? Prompt { get; set; }

    /// <summary>Saved values per translation provider id; secrets are encrypted.</summary>
    public Dictionary<string, Dictionary<string, string>> Providers { get; set; } = new();

    /// <summary>Saved values per subtitle source id; secrets are encrypted.</summary>
    public Dictionary<string, Dictionary<string, string>> SubtitleSources { get; set; } = new();

    public string EffectivePrompt => string.IsNullOrWhiteSpace(Prompt) ? TranslationPrompt.Default : Prompt;
}

/// <summary>Loads and saves <see cref="TranslationSettings"/> in the AppSettings table, encrypting secret fields.</summary>
public class TranslationSettingsStore(JitenDbContext context, IMemoryCache cache, IDataProtectionProvider dataProtection)
{
    public const string Key = "translation";
    private const string CacheKey = "app-settings:translation";
    private const string EncryptedPrefix = "enc:";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Jiten.Translation.Secrets");

    /// <summary>A fresh copy each call, so callers can edit it and pass it to <see cref="SaveAsync"/>.</summary>
    public async Task<TranslationSettings> GetAsync(CancellationToken ct = default)
    {
        if (!cache.TryGetValue(CacheKey, out string? json))
        {
            json = await context.AppSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";
            cache.Set(CacheKey, json, TimeSpan.FromMinutes(10));
        }

        return string.IsNullOrEmpty(json) ? new TranslationSettings() : JsonSerializer.Deserialize<TranslationSettings>(json) ?? new TranslationSettings();
    }

    public async Task SaveAsync(TranslationSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings);
        var row = await context.AppSettings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row == null)
            context.AppSettings.Add(new AppSetting { Key = Key, Value = json, UpdatedAt = DateTime.UtcNow });
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    /// <summary>The decrypted config for one provider's or source's saved values.</summary>
    public ProviderConfig Config(Dictionary<string, Dictionary<string, string>> group, string id, IReadOnlyList<SettingField> fields)
    {
        var saved = group.GetValueOrDefault(id) ?? new Dictionary<string, string>();
        var values = saved.ToDictionary(kv => kv.Key, kv => Reveal(kv.Value));
        return new ProviderConfig(values, fields);
    }

    /// <summary>
    /// Applies edits from the admin page: secrets left null keep their saved value, empty strings clear a value, and other
    /// values replace it (secrets encrypted).
    /// </summary>
    public void Apply(Dictionary<string, Dictionary<string, string>> group, string id, IReadOnlyList<SettingField> fields,
                      IReadOnlyDictionary<string, string?> edits)
    {
        var saved = group.GetValueOrDefault(id) ?? new Dictionary<string, string>();
        foreach (var field in fields)
        {
            if (!edits.TryGetValue(field.Key, out var value) || value == null) continue;
            if (value.Length == 0)
                saved.Remove(field.Key);
            else
                saved[field.Key] = field.Kind == SettingFieldKind.Secret ? EncryptedPrefix + _protector.Protect(value.Trim()) : value.Trim();
        }

        group[id] = saved;
    }

    /// <summary>A secret's last four characters, for "set (…KJ4)" on the admin page.</summary>
    public static string Hint(string secret) => secret.Length <= 8 ? "set" : $"…{secret[^4..]}";

    private string Reveal(string value)
    {
        if (!value.StartsWith(EncryptedPrefix)) return value;
        try
        {
            return _protector.Unprotect(value[EncryptedPrefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Encrypted with a key ring that's gone (see DataProtectionKeysPath); treat as unset so it can be re-entered.
            return "";
        }
    }
}
