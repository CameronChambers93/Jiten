using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>A note type's setup for Sync; see <see cref="AnkiNoteTypeSetup"/>.</summary>
/// <param name="Mining">How mining treats the note type; null on a save leaves it as it is, and on a read means the defaults.</param>
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
    int? ScriptVersion,
    AnkiMiningSettingsDto? Mining = null,
    AnkiAutoSentencesDto? AutoSentences = null);

public record AnkiSetupsDto(List<AnkiNoteTypeSetupDto> Setups, DateTime? KnownWordsSyncedAt);

/// <summary>Which parts of a sentence mining builds for a note type: the audio, the screenshot, the English, the video clip.</summary>
public record AnkiMiningPartsDto(bool Audio = true, bool Picture = true, bool English = true, bool Video = true);

/// <summary>
/// How mining treats a note type. <c>Existing</c>: when a word already has a card of this type, add the sentence to it
/// ("auto") or ask ("ask"). <c>EmptyFields</c> maps field roles to what mining does when it would leave them empty: "warn"
/// (add, and say so) or "deny" (don't add); null for the browser's defaults.
/// </summary>
public record AnkiMiningSettingsDto(string Existing = AnkiMiningExisting.Ask, AnkiMiningPartsDto? Parts = null,
                                    Dictionary<string, string>? EmptyFields = null);

/// <summary>
/// How Sync tops up a note type's cards with lines from imported anime, TV and film: each card gets up to
/// <c>PerCard</c> sentences, at most <c>PerSync</c> more in one Sync. A line qualifies when the word is its only word not
/// known (other words Young or better, or Mature with <c>OthersMature</c>), it has a clip and English when those are
/// needed, and its length is within range. <c>English</c> is a pipeline English choice: subtitlesOnly, subtitlesThenAi
/// or none. Null on a save leaves the settings as they are; <c>Enabled</c> false keeps them but turns it off.
/// </summary>
public record AnkiAutoSentencesDto(
    bool Enabled = false,
    int PerCard = 5,
    int PerSync = 2,
    bool OthersMature = false,
    bool NeedsClip = true,
    bool NeedsEnglish = true,
    int LengthMin = 6,
    int LengthMax = 40,
    bool Audio = true,
    bool Picture = true,
    bool Video = true,
    string English = "subtitlesOnly",
    List<AnkiRuleTitleDto>? ExcludedTitles = null);

/// <summary>A title a rule matches, with its name for showing the rule.</summary>
public record AnkiRuleTitleDto(int DeckId, string Title);

/// <summary>Which lines a new-card target takes: any of the media types, or any of the titles. Empty matches nothing.</summary>
public record AnkiCardTargetRuleDto(List<int>? MediaTypes = null, List<AnkiRuleTitleDto>? Titles = null);

/// <summary>One place new mined cards go. The field mapping is the note type's setup.</summary>
public record AnkiNewCardTargetDto(string DeckName, string ModelName, string Tags, AnkiCardTargetRuleDto? When = null);

/// <summary>
/// Where new mined cards go: the first target whose rule matches the line; the last has no rule and takes the rest.
/// The audio padding is shared.
/// </summary>
public record AnkiCardTargetsDto(List<AnkiNewCardTargetDto> Targets, int PadStartMs, int PadEndMs)
{
    public static AnkiCardTargetsDto Default => new([new AnkiNewCardTargetDto("", "", "jiten")], 250, 250);
}

/// <summary>The single target stored before there could be several; read once and converted.</summary>
internal record LegacyCardTarget(string DeckName, string ModelName, string Tags, int PadStartMs, int PadEndMs, Dictionary<string, string>? EmptyFields);

/// <param name="Previous">The value Sync read when it started; the update only applies if it's still that.</param>
public record KnownWordsSyncedRequest(DateTime SyncedAt, DateTime? Previous);

/// <summary>The user's Anki note type setups and new-card target, kept on the server so every device uses the same ones.</summary>
public class AnkiSyncSetupService(UserDbContext userContext)
{
    public const int MaxSetups = 50;
    public const int MaxFilterLength = 1000;
    public const int MaxPadMs = 5000;
    public const int MaxTargets = 20;
    public const int MaxRuleItems = 50;

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
        if (setup.Mining is { } m && !AnkiMiningExisting.All.Contains(m.Existing)) return "Unknown choice for a word's existing card.";
        if (setup.AutoSentences is { } auto && ValidateAuto(auto) is { } autoError) return autoError;

        var roles = setup.FieldRoles.Values.ToHashSet();
        if ((setup.KnownWords != null || setup.ImportSentences) && !roles.Contains("word")) return "Map a field to Word first.";
        if (setup.ImportSentences && !roles.Contains("sentence") && !roles.Contains("sentencePlain") && !roles.Contains("sentenceFurigana"))
            return "Importing sentences needs a Sentence field.";
        if (setup.SentenceList != AnkiSentenceListFormat.None && !roles.Contains("sentenceList")) return "Choose the field the sentence list goes in.";
        if (setup.UpdateMainFields && !roles.Overlaps(["sentence", "sentencePlain", "sentenceFurigana"]))
            return "Updating main fields needs a Sentence field.";
        if (setup.AutoSentences is { Enabled: true } && (setup.SentenceList == AnkiSentenceListFormat.None || !roles.Contains("sentenceList")))
            return "Automatic sentences need a sentence list (the rotation script or a plain list).";
        return null;
    }

    public const int MaxAutoPerCard = 20;
    public const int MaxAutoPerSync = 1000;
    public static readonly string[] AutoEnglish = ["subtitlesOnly", "subtitlesThenAi", "none"];

    private static string? ValidateAuto(AnkiAutoSentencesDto auto)
    {
        if (auto.PerCard is < 1 or > MaxAutoPerCard) return $"Sentences per card must be 1 to {MaxAutoPerCard}.";
        if (auto.PerSync is < 1 or > MaxAutoPerCard) return $"Sentences added per word in one Sync must be 1 to {MaxAutoPerCard}.";
        if (auto.LengthMin < 0 || auto.LengthMax < 1 || auto.LengthMin > auto.LengthMax || auto.LengthMax > 500) return "The length range isn't valid.";
        if (!AutoEnglish.Contains(auto.English)) return "Unknown English choice.";
        if (auto.ExcludedTitles is { Count: > MaxRuleItems }) return $"At most {MaxRuleItems} titles can be left out.";
        return null;
    }

    private static AnkiAutoSentencesDto CleanAuto(AnkiAutoSentencesDto a) =>
        a with
        {
            ExcludedTitles = (a.ExcludedTitles ?? []).Where(t => t.DeckId > 0).DistinctBy(t => t.DeckId).Take(MaxRuleItems)
                                                     .Select(t => t with { Title = Truncate((t.Title ?? "").Trim(), 300) }).ToList()
        };

    /// <summary>The setup's automatic sentences settings, or null when it has none (off).</summary>
    public static AnkiAutoSentencesDto? AutoSentences(AnkiNoteTypeSetup setup)
    {
        if (string.IsNullOrEmpty(setup.AutoSentencesJson)) return null;
        try
        {
            var a = JsonSerializer.Deserialize<AnkiAutoSentencesDto>(setup.AutoSentencesJson, Json);
            return a == null || ValidateAuto(a) != null ? null : CleanAuto(a);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The most sentences one Sync adds automatically, across every note type.</summary>
    public async Task<int> AutoMaxPerSyncAsync(string userId, CancellationToken ct) =>
        await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => (int?)s.AutoSentencesMaxPerSync).FirstOrDefaultAsync(ct) ?? 100;

    public async Task SaveAutoMaxPerSyncAsync(string userId, int max, CancellationToken ct)
    {
        if (max is < 1 or > MaxAutoPerSync) throw new ArgumentException($"The limit per Sync must be 1 to {MaxAutoPerSync}.");
        var settings = await SettingsAsync(userId, ct);
        settings.AutoSentencesMaxPerSync = max;
        await userContext.SaveChangesAsync(ct);
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
        if (dto.Mining != null) setup.MiningJson = JsonSerializer.Serialize(CleanMining(dto.Mining), Json);
        if (dto.AutoSentences != null) setup.AutoSentencesJson = JsonSerializer.Serialize(CleanAuto(dto.AutoSentences), Json);
        setup.UpdatedAt = DateTime.UtcNow;
        await userContext.SaveChangesAsync(ct);
        return ToDto(setup);
    }

    public async Task<bool> DeleteAsync(string userId, string modelName, CancellationToken ct) =>
        await userContext.AnkiNoteTypeSetups.Where(s => s.UserId == userId && s.ModelName == modelName).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// The new-card targets. A target saved before there could be several is converted on the first read: its empty-field
    /// policies move to its note type's setup, unless that setup has its own already.
    /// </summary>
    public async Task<AnkiCardTargetsDto> CardTargetAsync(string userId, CancellationToken ct)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.AnkiCardTargetJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(json) || json == "{}") return AnkiCardTargetsDto.Default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return AnkiCardTargetsDto.Default;
            if (doc.RootElement.TryGetProperty("targets", out _))
                return CleanTargets(JsonSerializer.Deserialize<AnkiCardTargetsDto>(json, Json) ?? AnkiCardTargetsDto.Default);
            var legacy = JsonSerializer.Deserialize<LegacyCardTarget>(json, Json);
            return legacy == null ? AnkiCardTargetsDto.Default : await ConvertLegacyAsync(userId, legacy, ct);
        }
        catch (JsonException)
        {
            return AnkiCardTargetsDto.Default;
        }
    }

    private async Task<AnkiCardTargetsDto> ConvertLegacyAsync(string userId, LegacyCardTarget legacy, CancellationToken ct)
    {
        var targets = CleanTargets(new AnkiCardTargetsDto([new AnkiNewCardTargetDto(legacy.DeckName ?? "", legacy.ModelName ?? "", legacy.Tags ?? "")],
                                                          legacy.PadStartMs, legacy.PadEndMs));
        var modelName = targets.Targets[0].ModelName;
        // Policies on a note type without a setup never applied: mining needs the setup's roles.
        if (legacy.EmptyFields is { Count: > 0 } policies && modelName != "")
        {
            var setup = await userContext.AnkiNoteTypeSetups.FirstOrDefaultAsync(s => s.UserId == userId && s.ModelName == modelName, ct);
            if (setup != null)
            {
                var mining = Mining(setup) ?? new AnkiMiningSettingsDto();
                if (mining.EmptyFields == null)
                    setup.MiningJson = JsonSerializer.Serialize(CleanMining(mining with { EmptyFields = policies }), Json);
            }
        }

        var settings = await SettingsAsync(userId, ct);
        settings.AnkiCardTargetJson = JsonSerializer.Serialize(targets, Json);
        await userContext.SaveChangesAsync(ct);
        return targets;
    }

    public async Task SaveCardTargetAsync(string userId, AnkiCardTargetsDto targets, CancellationToken ct)
    {
        var clean = Clean(targets);
        var settings = await SettingsAsync(userId, ct);
        settings.AnkiCardTargetJson = JsonSerializer.Serialize(clean, Json);
        await userContext.SaveChangesAsync(ct);
    }

    /// <summary>Why the targets can't be saved, or null when they can. Checks what <see cref="Clean"/> leaves.</summary>
    public static string? Validate(AnkiCardTargetsDto targets)
    {
        if (targets.Targets is { Count: > 0 and <= MaxTargets }) targets = Clean(targets);
        if (targets.Targets is not { Count: > 0 }) return "Add a target for new cards.";
        if (targets.Targets.Count > MaxTargets) return $"At most {MaxTargets} targets.";
        for (var i = 0; i < targets.Targets.Count - 1; i++)
        {
            var when = targets.Targets[i].When;
            if (when == null || ((when.MediaTypes?.Count ?? 0) == 0 && (when.Titles?.Count ?? 0) == 0))
                return $"Target {i + 1} needs a media type or a title; only the last one takes everything else.";
        }

        return null;
    }

    /// <summary>The targets trimmed and capped, with unknown media types and titles dropped and no rule on the last.</summary>
    public static AnkiCardTargetsDto Clean(AnkiCardTargetsDto targets) => CleanTargets(targets);

    private static AnkiCardTargetsDto CleanTargets(AnkiCardTargetsDto targets)
    {
        var list = (targets.Targets ?? []).Take(MaxTargets).Select((t, i) => new AnkiNewCardTargetDto(
            Truncate((t.DeckName ?? "").Trim(), 200), Truncate((t.ModelName ?? "").Trim(), 200), Truncate((t.Tags ?? "").Trim(), 500),
            i == targets.Targets!.Count - 1 ? null : CleanRule(t.When))).ToList();
        if (list.Count == 0) list.Add(AnkiCardTargetsDto.Default.Targets[0]);
        return new AnkiCardTargetsDto(list, Math.Clamp(targets.PadStartMs, 0, MaxPadMs), Math.Clamp(targets.PadEndMs, 0, MaxPadMs));
    }

    private static AnkiCardTargetRuleDto? CleanRule(AnkiCardTargetRuleDto? rule) =>
        rule == null
            ? null
            : new AnkiCardTargetRuleDto(
                (rule.MediaTypes ?? []).Where(m => Enum.IsDefined(typeof(MediaType), m)).Distinct().Take(MaxRuleItems).ToList(),
                (rule.Titles ?? []).Where(t => t.DeckId > 0).DistinctBy(t => t.DeckId).Take(MaxRuleItems)
                                   .Select(t => t with { Title = Truncate((t.Title ?? "").Trim(), 300) }).ToList());

    private static AnkiMiningSettingsDto CleanMining(AnkiMiningSettingsDto m) =>
        new(AnkiMiningExisting.All.Contains(m.Existing) ? m.Existing : AnkiMiningExisting.Ask,
            m.Parts ?? new AnkiMiningPartsDto(),
            m.EmptyFields?.Where(p => AnkiFieldRoles.All.Contains(p.Key) && p.Value is "warn" or "deny").ToDictionary(p => p.Key, p => p.Value));

    /// <summary>The setup's mining settings, or null for the defaults.</summary>
    public static AnkiMiningSettingsDto? Mining(AnkiNoteTypeSetup setup)
    {
        if (string.IsNullOrEmpty(setup.MiningJson)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<AnkiMiningSettingsDto>(setup.MiningJson, Json);
            return m == null ? null : CleanMining(m);
        }
        catch (JsonException)
        {
            return null;
        }
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
            s.SentenceList, s.ScriptVersion, Mining(s), AutoSentences(s));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
