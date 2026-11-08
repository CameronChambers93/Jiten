using System.Text.Json;
using System.Text.Json.Serialization;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>
/// When a step runs. The sentence must match the media types or titles (either; both empty matches every line), and the
/// word's existing Anki card must be on one of the note types (empty: any word, with or without a card).
/// </summary>
public record PipelineConditionDto(List<int>? MediaTypes = null, List<AnkiRuleTitleDto>? Titles = null, List<string>? CardNoteTypes = null);

/// <summary>Where an Anki step puts a new note: the first target whose rule matches the line; the last takes the rest.</summary>
public record PipelineTargetDto(string? DeckName, string? ModelName, AnkiCardTargetRuleDto? When = null);

/// <summary>
/// One step of a pipeline. <c>Kind</c> is build, anki or tags; only that kind's options are kept, the rest are null.
/// <list type="bullet">
/// <item>build: the parts made for the card (audio, picture, video), its English (<see cref="PipelineEnglish"/>), and the audio padding.</item>
/// <item>anki: what happens when the word has a card (<see cref="PipelineHasCard"/>) and when it hasn't (<see cref="PipelineNoCard"/>), and the targets for a new note.</item>
/// <item>tags: tags for a new note, plus the line's title and media type when asked.</item>
/// </list>
/// </summary>
public record PipelineStepDto(
    string? Kind,
    PipelineConditionDto? When = null,
    bool? Audio = null,
    bool? Picture = null,
    bool? Video = null,
    string? English = null,
    int? PadStartMs = null,
    int? PadEndMs = null,
    string? HasCard = null,
    string? NoCard = null,
    List<PipelineTargetDto>? Targets = null,
    string? Tags = null,
    bool? TitleTag = null,
    bool? MediaTypeTag = null);

/// <summary>A named list of steps the sentence button runs, after saving the sentence to the word's list.</summary>
public record PipelineDto(string? Id, string? Name, List<PipelineStepDto>? Steps);

/// <summary>The pipeline each page's sentence button runs; pipeline ids.</summary>
public record PipelineDefaultsDto(string? Word, string? Mine, string? DeckSentences, string? Sentence);

/// <param name="Saved">False when the pipelines were built from the older card settings and haven't been saved yet.</param>
public record MiningPipelinesDto(List<PipelineDto>? Pipelines, PipelineDefaultsDto? Defaults, bool Saved = true);

public static class PipelineStepKind
{
    public const string Build = "build";
    public const string Anki = "anki";
    public const string Tags = "tags";
    public static readonly string[] All = [Build, Anki, Tags];
}

public static class PipelineEnglish
{
    public const string SubtitlesThenAi = "subtitlesThenAi";
    public const string SubtitlesOnly = "subtitlesOnly";
    public const string None = "none";
    public static readonly string[] All = [SubtitlesThenAi, SubtitlesOnly, None];
}

public static class PipelineHasCard
{
    /// <summary>Add the sentence to the word's card.</summary>
    public const string Add = "add";
    /// <summary>Ask: the word's card or a new note.</summary>
    public const string Ask = "ask";
    /// <summary>Make a new note from the targets.</summary>
    public const string New = "new";
    public const string Skip = "skip";
    public static readonly string[] All = [Add, Ask, New, Skip];
}

public static class PipelineNoCard
{
    public const string Targets = "targets";
    public const string Skip = "skip";
    public static readonly string[] All = [Targets, Skip];
}

/// <summary>
/// The user's sentence pipelines (n1miner), kept on the server so every device runs the same ones. The browser runs
/// them; see <c>usePipelines</c> and <c>useMineSentence</c>.
/// </summary>
public class MiningPipelineService(UserDbContext userContext, AnkiSyncSetupService setups)
{
    public const int MaxPipelines = 20;
    public const int MaxSteps = 12;
    public const int MaxNameLength = 60;
    public const int MaxConditionItems = 50;

    public const string MineId = "mine";
    public const string KeepId = "keep";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The saved pipelines, or, before the first save, the ones built from the card target and note type setups.</summary>
    public async Task<MiningPipelinesDto> GetAsync(string userId, CancellationToken ct)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.MiningPipelinesJson).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(json) && json != "{}")
        {
            try
            {
                var saved = JsonSerializer.Deserialize<MiningPipelinesDto>(json, Json);
                if (saved?.Pipelines is { Count: > 0 }) return Clean(saved) with { Saved = true };
            }
            catch (JsonException)
            {
                // Falls back to the pipelines built from the older settings.
            }
        }

        return await FromCardSettingsAsync(userId, ct);
    }

    public async Task<MiningPipelinesDto> SaveAsync(string userId, MiningPipelinesDto pipelines, CancellationToken ct)
    {
        var clean = Clean(pipelines) with { Saved = true };
        var settings = userContext.UserSettings.Local.FirstOrDefault(s => s.UserId == userId)
                       ?? await userContext.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings == null) userContext.UserSettings.Add(settings = new UserSettings { UserId = userId });
        settings.MiningPipelinesJson = JsonSerializer.Serialize(clean with { Saved = true }, Json);
        await userContext.SaveChangesAsync(ct);
        return clean;
    }

    /// <summary>
    /// "Mine" and "Keep for Jiten" from the card settings before pipelines: one Build step with the first target's note
    /// type's parts and the padding; an Anki step adding automatically for each note type set to, then one that asks, with
    /// the targets; the targets' tags. Nothing is written: the older settings stay as they are.
    /// </summary>
    public async Task<MiningPipelinesDto> FromCardSettingsAsync(string userId, CancellationToken ct)
    {
        var targets = await setups.CardTargetAsync(userId, ct);
        var noteTypes = await setups.SetupsAsync(userId, ct);
        var mining = noteTypes.ToDictionary(s => s.ModelName, s => AnkiSyncSetupService.Mining(s) ?? new AnkiMiningSettingsDto());

        var steps = new List<PipelineStepDto>();
        var firstModel = targets.Targets[0].ModelName;
        var parts = (mining.GetValueOrDefault(firstModel) ?? new AnkiMiningSettingsDto()).Parts ?? new AnkiMiningPartsDto();
        steps.Add(new PipelineStepDto(PipelineStepKind.Build, Audio: parts.Audio, Picture: parts.Picture, Video: parts.Video,
                                      English: parts.English ? PipelineEnglish.SubtitlesThenAi : PipelineEnglish.None,
                                      PadStartMs: targets.PadStartMs, PadEndMs: targets.PadEndMs));

        foreach (var (modelName, m) in mining.Where(m => m.Value.Existing == AnkiMiningExisting.Auto).OrderBy(m => m.Key, StringComparer.Ordinal))
            steps.Add(new PipelineStepDto(PipelineStepKind.Anki, new PipelineConditionDto(CardNoteTypes: [modelName]),
                                          HasCard: PipelineHasCard.Add, NoCard: PipelineNoCard.Skip));

        steps.Add(new PipelineStepDto(PipelineStepKind.Anki, HasCard: PipelineHasCard.Ask, NoCard: PipelineNoCard.Targets,
                                      Targets: targets.Targets.Select(t => new PipelineTargetDto(t.DeckName, t.ModelName, t.When)).ToList()));

        // One Tags step when every target has the same tags; otherwise each target's tags with its rule, the last's always.
        var tagSets = targets.Targets.Select(t => t.Tags.Trim()).ToList();
        if (tagSets.Distinct().Count() == 1)
        {
            if (tagSets[0] != "") steps.Add(new PipelineStepDto(PipelineStepKind.Tags, Tags: tagSets[0], TitleTag: false, MediaTypeTag: false));
        }
        else
        {
            foreach (var t in targets.Targets.Where(t => t.Tags.Trim() != ""))
                steps.Add(new PipelineStepDto(PipelineStepKind.Tags,
                                              t.When == null ? null : new PipelineConditionDto(t.When.MediaTypes, t.When.Titles),
                                              Tags: t.Tags.Trim(), TitleTag: false, MediaTypeTag: false));
        }

        var result = new MiningPipelinesDto([new PipelineDto(MineId, "Mine", steps), new PipelineDto(KeepId, "Keep for Jiten", [])],
                                            new PipelineDefaultsDto(MineId, MineId, MineId, MineId), Saved: false);
        return Clean(result) with { Saved = false };
    }

    /// <summary>Why the pipelines can't be saved, or null when they can. Checks what <see cref="Clean"/> leaves.</summary>
    public static string? Validate(MiningPipelinesDto pipelines)
    {
        if (pipelines.Pipelines is not { Count: > 0 }) return "Keep at least one pipeline.";
        if (pipelines.Pipelines.Count > MaxPipelines) return $"At most {MaxPipelines} pipelines.";
        foreach (var p in pipelines.Pipelines)
        {
            var name = (p.Name ?? "").Trim();
            if (name == "") return "Every pipeline needs a name.";
            if (name.Length > MaxNameLength) return $"\"{name[..20]}…\" is over {MaxNameLength} characters.";
            if ((p.Steps?.Count ?? 0) > MaxSteps) return $"{name}: at most {MaxSteps} steps.";
            for (var i = 0; i < (p.Steps?.Count ?? 0); i++)
            {
                var step = p.Steps![i];
                var at = $"{name}, step {i + 1}";
                if (!PipelineStepKind.All.Contains(step.Kind)) return $"{at}: unknown kind of step.";
                if (step.Kind == PipelineStepKind.Build && step.English != null && !PipelineEnglish.All.Contains(step.English))
                    return $"{at}: unknown English choice.";
                if (step.Kind != PipelineStepKind.Anki) continue;
                if (step.HasCard != null && !PipelineHasCard.All.Contains(step.HasCard)) return $"{at}: unknown choice for a word with a card.";
                if (step.NoCard != null && !PipelineNoCard.All.Contains(step.NoCard)) return $"{at}: unknown choice for a word without a card.";
                if (!NeedsTargets(step)) continue;
                var list = step.Targets ?? [];
                if (list.Count == 0) return $"{at}: add a target for new cards.";
                if (list.Count > AnkiSyncSetupService.MaxTargets) return $"{at}: at most {AnkiSyncSetupService.MaxTargets} targets.";
                for (var t = 0; t < list.Count; t++)
                {
                    if (string.IsNullOrWhiteSpace(list[t].DeckName) || string.IsNullOrWhiteSpace(list[t].ModelName))
                        return $"{at}: target {t + 1} needs a deck and a note type.";
                    var when = list[t].When;
                    if (t < list.Count - 1 && (when == null || ((when.MediaTypes?.Count ?? 0) == 0 && (when.Titles?.Count ?? 0) == 0)))
                        return $"{at}: target {t + 1} needs a media type or a title; only the last one takes everything else.";
                }
            }
        }

        var ids = pipelines.Pipelines.Select(p => p.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count != ids.Distinct().Count()) return "Two pipelines have the same id.";
        return null;
    }

    /// <summary>Whether an Anki step can make a new note, so needs targets; a choice left out is the one Clean gives it.</summary>
    private static bool NeedsTargets(PipelineStepDto step) =>
        (step.NoCard ?? PipelineNoCard.Targets) == PipelineNoCard.Targets || (step.HasCard ?? PipelineHasCard.Ask) is PipelineHasCard.Ask or PipelineHasCard.New;

    /// <summary>
    /// The pipelines trimmed and capped: ids given to new ones, each step keeping only its kind's options (with defaults),
    /// unknown media types and titles dropped, and defaults pointing at a pipeline that exists (else the first).
    /// </summary>
    public static MiningPipelinesDto Clean(MiningPipelinesDto pipelines)
    {
        var used = new HashSet<string>();
        var list = (pipelines.Pipelines ?? []).Take(MaxPipelines).Select(p =>
        {
            var id = Truncate((p.Id ?? "").Trim(), 40);
            if (id == "" || !used.Add(id)) used.Add(id = Guid.NewGuid().ToString("N")[..12]);
            var name = Truncate((p.Name ?? "").Trim(), MaxNameLength);
            return new PipelineDto(id, name == "" ? "Pipeline" : name, (p.Steps ?? []).Take(MaxSteps).Where(s => PipelineStepKind.All.Contains(s.Kind)).Select(CleanStep).ToList());
        }).ToList();
        if (list.Count == 0) list.Add(new PipelineDto(KeepId, "Keep for Jiten", []));

        var ids = list.Select(p => p.Id).ToHashSet();
        string Pick(string? id) => id != null && ids.Contains(id) ? id : list[0].Id!;
        var d = pipelines.Defaults;
        return new MiningPipelinesDto(list, new PipelineDefaultsDto(Pick(d?.Word), Pick(d?.Mine), Pick(d?.DeckSentences), Pick(d?.Sentence)), pipelines.Saved);
    }

    private static PipelineStepDto CleanStep(PipelineStepDto s)
    {
        var when = CleanCondition(s.When);
        return s.Kind switch
        {
            PipelineStepKind.Build => new PipelineStepDto(PipelineStepKind.Build, when, s.Audio ?? true, s.Picture ?? true, s.Video ?? true,
                                                          PipelineEnglish.All.Contains(s.English) ? s.English : PipelineEnglish.SubtitlesThenAi,
                                                          Math.Clamp(s.PadStartMs ?? 250, 0, AnkiSyncSetupService.MaxPadMs),
                                                          Math.Clamp(s.PadEndMs ?? 250, 0, AnkiSyncSetupService.MaxPadMs)),
            PipelineStepKind.Anki => CleanAnki(s, when),
            _ => new PipelineStepDto(PipelineStepKind.Tags, when, Tags: Truncate((s.Tags ?? "").Trim(), 500), TitleTag: s.TitleTag ?? false,
                                     MediaTypeTag: s.MediaTypeTag ?? false),
        };
    }

    private static PipelineStepDto CleanAnki(PipelineStepDto s, PipelineConditionDto? when)
    {
        var hasCard = PipelineHasCard.All.Contains(s.HasCard) ? s.HasCard! : PipelineHasCard.Ask;
        var noCard = PipelineNoCard.All.Contains(s.NoCard) ? s.NoCard! : PipelineNoCard.Targets;
        var raw = (s.Targets ?? []).Take(AnkiSyncSetupService.MaxTargets).ToList();
        // The targets' rules and padding are cleaned the same way as the card target's.
        var targets = raw.Count == 0
            ? []
            : AnkiSyncSetupService.Clean(new AnkiCardTargetsDto(raw.Select(t => new AnkiNewCardTargetDto(t.DeckName ?? "", t.ModelName ?? "", "", t.When)).ToList(), 0, 0))
                                  .Targets.Select(t => new PipelineTargetDto(t.DeckName, t.ModelName, t.When)).ToList();
        return new PipelineStepDto(PipelineStepKind.Anki, when, HasCard: hasCard, NoCard: noCard, Targets: targets);
    }

    private static PipelineConditionDto? CleanCondition(PipelineConditionDto? c)
    {
        if (c == null) return null;
        var media = (c.MediaTypes ?? []).Where(m => Enum.IsDefined(typeof(MediaType), m)).Distinct().Take(MaxConditionItems).ToList();
        var titles = (c.Titles ?? []).Where(t => t.DeckId > 0).DistinctBy(t => t.DeckId).Take(MaxConditionItems)
                                    .Select(t => t with { Title = Truncate((t.Title ?? "").Trim(), 300) }).ToList();
        var noteTypes = (c.CardNoteTypes ?? []).Select(n => Truncate((n ?? "").Trim(), 200)).Where(n => n != "").Distinct().Take(MaxConditionItems).ToList();
        return media.Count == 0 && titles.Count == 0 && noteTypes.Count == 0 ? null : new PipelineConditionDto(media, titles, noteTypes);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
