using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>A note type's setup for Sync; see <see cref="AnkiNoteTypeSetup"/>.</summary>
public record AnkiNoteTypeSetupDto(
    string ModelName,
    string? Filter,
    Dictionary<string, string> FieldRoles,
    string? Preset,
    string? KnownWords,
    bool ImportSentences,
    bool TakeFlags,
    bool FoldDuplicates,
    bool UpdateMainFields,
    string SentenceList,
    int? ScriptVersion);

public record AnkiSetupsDto(List<AnkiNoteTypeSetupDto> Setups, DateTime? KnownWordsSyncedAt);

/// <summary>Where new mined cards go. The field mapping is the note type's setup.</summary>
public record AnkiCardTargetDto(string DeckName, string ModelName, string Tags, int PadStartMs, int PadEndMs)
{
    public static readonly AnkiCardTargetDto Default = new("", "", "jiten", 250, 250);
}

/// <param name="Previous">The value Sync read when it started; the update only applies if it's still that.</param>
public record KnownWordsSyncedRequest(DateTime SyncedAt, DateTime? Previous);

/// <summary>The user's Anki note type setups and new-card target, kept on the server so every device uses the same ones.</summary>
public class AnkiSyncSetupService(UserDbContext userContext)
{
    public const int MaxSetups = 50;
    public const int MaxFilterLength = 1000;
    public const int MaxPadMs = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AnkiSetupsDto> ListAsync(string userId, CancellationToken ct)
    {
        var setups = await userContext.AnkiNoteTypeSetups.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.ModelName).ToListAsync(ct);
        var syncedAt = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId)
                                        .Select(s => s.AnkiKnownWordsSyncedAt).FirstOrDefaultAsync(ct);
        return new AnkiSetupsDto(setups.Select(ToDto).ToList(), syncedAt);
    }

    public async Task<List<AnkiNoteTypeSetup>> SetupsAsync(string userId, CancellationToken ct) =>
        await userContext.AnkiNoteTypeSetups.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);

    /// <summary>Why the setup can't be saved, or null when it can.</summary>
    public static string? Validate(AnkiNoteTypeSetupDto setup)
    {
        if (string.IsNullOrWhiteSpace(setup.ModelName) || setup.ModelName.Length > 200) return "The note type name is missing or too long.";
        if (setup.Filter is { Length: > MaxFilterLength }) return $"The filter is over {MaxFilterLength} characters.";
        if (setup.FieldRoles.Count > 200) return "Too many fields.";
        if (setup.FieldRoles.Any(r => r.Key.Length > 200 || (r.Value != "" && !AnkiFieldRoles.All.Contains(r.Value))))
            return "Unknown field role.";
        if (setup.KnownWords != null && !AnkiKnownWordsState.All.Contains(setup.KnownWords)) return "Unknown known-words state.";
        if (!AnkiSentenceListFormat.All.Contains(setup.SentenceList)) return "Unknown sentence list format.";
        if (setup.Preset is { Length: > 50 }) return "The preset name is too long.";

        var roles = setup.FieldRoles.Values.ToHashSet();
        if ((setup.KnownWords != null || setup.ImportSentences) && !roles.Contains("word")) return "Map a field to Word first.";
        if (setup.ImportSentences && !roles.Contains("sentence") && !roles.Contains("sentencePlain") && !roles.Contains("sentenceFurigana"))
            return "Importing sentences needs a Sentence field.";
        if (setup.SentenceList != AnkiSentenceListFormat.None && !roles.Contains("sentenceList")) return "Choose the field the sentence list goes in.";
        if (setup.UpdateMainFields && !roles.Overlaps(["sentence", "sentencePlain", "sentenceFurigana"]))
            return "Updating main fields needs a Sentence field.";
        return null;
    }

    public async Task<AnkiNoteTypeSetupDto> SaveAsync(string userId, AnkiNoteTypeSetupDto dto, CancellationToken ct)
    {
        var setup = await userContext.AnkiNoteTypeSetups.FirstOrDefaultAsync(s => s.UserId == userId && s.ModelName == dto.ModelName, ct);
        if (setup == null)
        {
            if (await userContext.AnkiNoteTypeSetups.CountAsync(s => s.UserId == userId, ct) >= MaxSetups)
                throw new ArgumentException($"At most {MaxSetups} note types can be synced.");
            userContext.AnkiNoteTypeSetups.Add(setup = new AnkiNoteTypeSetup { UserId = userId, ModelName = dto.ModelName });
        }

        var filter = string.IsNullOrWhiteSpace(dto.Filter) ? null : dto.Filter.Trim();
        var roles = dto.FieldRoles.Where(r => r.Value != "").ToDictionary(r => r.Key, r => r.Value);
        var rolesJson = JsonSerializer.Serialize(roles, Json);

        // A word known through a note that no longer counts stays known, but a note that now counts must be sent, so the
        // next Sync sends every card again.
        var knownChanged = dto.KnownWords != setup.KnownWords
                           || (dto.KnownWords != null && (filter != setup.Filter || Role(roles, "word") != Role(Roles(setup), "word")
                                                          || Role(roles, "wordReading") != Role(Roles(setup), "wordReading")));
        if (knownChanged && dto.KnownWords != null) await ResetKnownWordsAsync(userId, ct);

        setup.Filter = filter;
        setup.FieldRolesJson = rolesJson;
        setup.Preset = dto.Preset;
        setup.KnownWords = dto.KnownWords;
        setup.ImportSentences = dto.ImportSentences;
        setup.TakeFlags = dto.TakeFlags;
        setup.FoldDuplicates = dto.FoldDuplicates;
        setup.UpdateMainFields = dto.UpdateMainFields;
        setup.SentenceList = dto.SentenceList;
        setup.ScriptVersion = dto.ScriptVersion;
        setup.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);
        return ToDto(setup);
    }

    public async Task<bool> DeleteAsync(string userId, string modelName, CancellationToken ct) =>
        await userContext.AnkiNoteTypeSetups.Where(s => s.UserId == userId && s.ModelName == modelName).ExecuteDeleteAsync(ct) > 0;

    public async Task<AnkiCardTargetDto> CardTargetAsync(string userId, CancellationToken ct)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.AnkiCardTargetJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(json) || json == "{}") return AnkiCardTargetDto.Default;
        try
        {
            return JsonSerializer.Deserialize<AnkiCardTargetDto>(json, Json) ?? AnkiCardTargetDto.Default;
        }
        catch (JsonException)
        {
            return AnkiCardTargetDto.Default;
        }
    }

    public async Task SaveCardTargetAsync(string userId, AnkiCardTargetDto target, CancellationToken ct)
    {
        var clean = new AnkiCardTargetDto(Truncate(target.DeckName.Trim(), 200), Truncate(target.ModelName.Trim(), 200), Truncate(target.Tags.Trim(), 500),
                                          Math.Clamp(target.PadStartMs, 0, MaxPadMs), Math.Clamp(target.PadEndMs, 0, MaxPadMs));
        var settings = await SettingsAsync(userId, ct);
        settings.AnkiCardTargetJson = JsonSerializer.Serialize(clean, Json);
        await userContext.SaveChangesAsync(ct);
    }

    public async Task<bool> SetKnownWordsSyncedAsync(string userId, DateTime syncedAt, DateTime? previous, CancellationToken ct)
    {
        var settings = await SettingsAsync(userId, ct);
        var current = settings.AnkiKnownWordsSyncedAt;
        if (current is { } c && (previous is not { } p || Math.Abs((c - p.ToUniversalTime()).TotalMilliseconds) > 1)) return false;
        if (current == null && previous != null) return false;
        settings.AnkiKnownWordsSyncedAt = DateTime.SpecifyKind(syncedAt.ToUniversalTime(), DateTimeKind.Utc);
        await userContext.SaveChangesAsync(ct);
        return true;
    }

    private async Task ResetKnownWordsAsync(string userId, CancellationToken ct)
    {
        var settings = await SettingsAsync(userId, ct);
        settings.AnkiKnownWordsSyncedAt = null;
    }

    private async Task<UserSettings> SettingsAsync(string userId, CancellationToken ct)
    {
        var settings = userContext.UserSettings.Local.FirstOrDefault(s => s.UserId == userId)
                       ?? await userContext.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings == null) userContext.UserSettings.Add(settings = new UserSettings { UserId = userId });
        return settings;
    }

    public static Dictionary<string, string> Roles(AnkiNoteTypeSetup setup)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(setup.FieldRolesJson, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The first field holding the role, or null.</summary>
    public static string? Role(IReadOnlyDictionary<string, string> roles, string role) => roles.FirstOrDefault(r => r.Value == role).Key;

    private static AnkiNoteTypeSetupDto ToDto(AnkiNoteTypeSetup s) =>
        new(s.ModelName, s.Filter, Roles(s), s.Preset, s.KnownWords, s.ImportSentences, s.TakeFlags, s.FoldDuplicates, s.UpdateMainFields,
            s.SentenceList, s.ScriptVersion);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
