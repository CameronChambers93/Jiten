using System.Diagnostics;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Api.Services.Translation;
using Jiten.Api.Services.Translation.Subtitles;
using Jiten.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Controllers;

public record SettingFieldDto(string Key, string Label, string Kind, string? Default, string[]? Options, string? Help, string? Value, bool IsSet, string? Hint);

public record SettingGroupDto(string Id, string Name, List<SettingFieldDto> Fields);

public record TranslationSettingsDto(string CardMode, string PageMode, bool PageAutoAi, string ActiveProvider, int ContextLines, string Prompt,
                                     string DefaultPrompt, List<SettingGroupDto> Providers, List<SettingGroupDto> SubtitleSources);

/// <param name="Prompt">Empty or the default text resets to the default prompt.</param>
/// <param name="Providers">Per provider id, field edits: null keeps a value, "" clears it.</param>
public record UpdateTranslationSettingsRequest(string CardMode, string PageMode, bool PageAutoAi, string ActiveProvider, int ContextLines,
                                               string? Prompt, Dictionary<string, Dictionary<string, string?>>? Providers,
                                               Dictionary<string, Dictionary<string, string?>>? SubtitleSources);

public record TranslationTestRequest(string? Sentence, string? Title);

public record SubtitleSearchRequest(string SourceId, string Query, int? Season);

public record SubtitlePreviewRequest(List<PreviewRequestItem> Items);

public record SubtitleSaveRequest(List<SaveRequestItem> Items);

/// <summary>Admin translation settings, English subtitle lookup per title, and bulk AI translation.</summary>
[ApiController]
[Route("api/admin/translation")]
[Authorize("RequiresAdmin")]
public class AdminTranslationController(
    JitenDbContext context,
    TranslationSettingsStore settingsStore,
    IEnumerable<ITranslationProvider> providers,
    SentenceTranslationService translations,
    EnglishSubtitleService englishSubtitles,
    IBackgroundJobClient backgroundJobs,
    IMemoryCache cache) : ControllerBase
{
    /// <summary>Rough tokens per AI call for the estimate: GSM's prompt with a few context lines in, a line (and some thinking) out.</summary>
    private const int EstimatedInputTokens = 700;
    private const int EstimatedOutputTokens = 200;

    private const string TestSentence = "お前ならできる！";

    [HttpGet("settings")]
    public async Task<TranslationSettingsDto> GetSettings(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        return new TranslationSettingsDto(settings.CardMode, settings.PageMode, settings.PageAutoAi, settings.ActiveProvider, settings.ContextLines,
                                          settings.EffectivePrompt, TranslationPrompt.Default,
                                          providers.Select(p => Group(settings.Providers, p.Id, p.Name, p.Fields)).ToList(),
                                          englishSubtitles.Sources.Select(s => Group(settings.SubtitleSources, s.Id, s.Name, s.Fields)).ToList());
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateTranslationSettingsRequest request, CancellationToken ct)
    {
        if (!CardTranslationMode.All.Contains(request.CardMode) || !PageTranslationMode.All.Contains(request.PageMode))
            return BadRequest(new { message = "Unknown card or page mode" });
        if (request.ActiveProvider.Length > 0 && providers.All(p => p.Id != request.ActiveProvider))
            return BadRequest(new { message = "Unknown provider" });

        var settings = await settingsStore.GetAsync(ct);
        settings.CardMode = request.CardMode;
        settings.PageMode = request.PageMode;
        settings.PageAutoAi = request.PageAutoAi;
        settings.ActiveProvider = request.ActiveProvider;
        settings.ContextLines = Math.Clamp(request.ContextLines, 0, 20);
        settings.Prompt = string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Trim() == TranslationPrompt.Default.Trim() ? null : request.Prompt;

        foreach (var provider in providers)
            if (request.Providers?.GetValueOrDefault(provider.Id) is { } edits)
                settingsStore.Apply(settings.Providers, provider.Id, provider.Fields, edits);
        foreach (var source in englishSubtitles.Sources)
            if (request.SubtitleSources?.GetValueOrDefault(source.Id) is { } edits)
                settingsStore.Apply(settings.SubtitleSources, source.Id, source.Fields, edits);

        await settingsStore.SaveAsync(settings, ct);
        return Ok(await GetSettings(ct));
    }

    [HttpGet("providers/{providerId}/models")]
    public async Task<IActionResult> ListModels(string providerId, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Id == providerId);
        if (provider == null) return NotFound();

        var settings = await settingsStore.GetAsync(ct);
        var config = settingsStore.Config(settings.Providers, provider.Id, provider.Fields);
        if (!config.HasSecrets) return BadRequest(new { message = $"Save {provider.Name}'s API key first." });

        try
        {
            return Ok(new { models = await provider.ListModelsAsync(config, ct) });
        }
        catch (Exception e) when (e is TranslationProviderException or HttpRequestException or Anthropic.Exceptions.AnthropicApiException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }

    /// <summary>Translates a sample line with the saved settings; nothing is cached.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TranslationTestRequest request, CancellationToken ct)
    {
        var active = await translations.ActiveProviderAsync(ct);
        if (active == null) return BadRequest(new { message = "Choose a provider and save its API key first." });

        var settings = await settingsStore.GetAsync(ct);
        var prompt = TranslationPrompt.Render(settings.EffectivePrompt, request.Title ?? "おそ松さん", [], request.Sentence ?? TestSentence);
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await active.Value.Provider.TranslateAsync(active.Value.Config, prompt, ct);
            return Ok(new { result.Text, result.Model, result.InputTokens, result.OutputTokens, ElapsedMs = watch.ElapsedMilliseconds });
        }
        catch (TranslationProviderException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var byModel = await context.ExampleSentenceTranslations.AsNoTracking()
                                   .GroupBy(t => new { t.Provider, t.Model })
                                   .Select(g => new { g.Key.Provider, g.Key.Model, Count = g.Count() })
                                   .ToListAsync(ct);
        var englishTracks = await context.DeckEnglishTracks.CountAsync(ct);
        return Ok(new { aiTranslations = byModel.Sum(m => m.Count), byModel, englishTracks });
    }

    /// <summary>Deletes cached AI translations (all, or one title's), e.g. after changing the prompt or model.</summary>
    [HttpDelete("cache")]
    public async Task<IActionResult> ClearCache([FromQuery] int? deckId, CancellationToken ct)
    {
        var query = context.ExampleSentenceTranslations.AsQueryable();
        if (deckId != null)
        {
            var deckIds = await DeckAndChildren(deckId.Value, ct);
            query = query.Where(t => deckIds.Contains(t.DeckId));
        }

        return Ok(new { deleted = await query.ExecuteDeleteAsync(ct) });
    }

    [HttpGet("titles/{deckId:int}/english")]
    public async Task<IActionResult> TitleEnglish(int deckId, CancellationToken ct) =>
        await englishSubtitles.InfoAsync(deckId, ct) is { } info ? Ok(info) : NotFound();

    [HttpPost("titles/{deckId:int}/english/search")]
    public async Task<IActionResult> SearchSubtitles(int deckId, [FromBody] SubtitleSearchRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(new { candidates = await englishSubtitles.SearchAsync(deckId, request.SourceId, request.Query, request.Season, ct) });
        }
        catch (Exception e) when (e is SubtitleSourceException or HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
    }

    [HttpPost("titles/{deckId:int}/english/preview")]
    public async Task<IActionResult> PreviewSubtitles(int deckId, [FromBody] SubtitlePreviewRequest request, CancellationToken ct)
    {
        var episodes = await DeckAndChildren(deckId, ct);
        if (request.Items.Any(i => !episodes.Contains(i.DeckId))) return BadRequest(new { message = "Episode not in this title" });
        return Ok(new { previews = await englishSubtitles.PreviewAsync(request.Items, ct) });
    }

    [HttpPost("titles/{deckId:int}/english/save")]
    public async Task<IActionResult> SaveSubtitles(int deckId, [FromBody] SubtitleSaveRequest request, CancellationToken ct)
    {
        var episodes = await DeckAndChildren(deckId, ct);
        if (request.Items.Any(i => !episodes.Contains(i.DeckId))) return BadRequest(new { message = "Episode not in this title" });
        return Ok(new { saved = await englishSubtitles.SaveAsync(request.Items, ct) });
    }

    [HttpDelete("decks/{deckId:int}/english")]
    public async Task<IActionResult> DeleteSubtitles(int deckId, CancellationToken ct) =>
        await englishSubtitles.DeleteAsync(deckId, ct) ? NoContent() : NotFound();

    /// <summary>How the title's sentences would be translated, what the AI part would cost, and any running job's progress.</summary>
    [HttpGet("titles/{deckId:int}/estimate")]
    public async Task<IActionResult> Estimate(int deckId, CancellationToken ct)
    {
        int sentences = 0, fromSubtitles = 0, cachedAi = 0, needAi = 0;
        foreach (var id in await DeckAndChildren(deckId, ct))
        {
            var coverage = await translations.CoverageAsync(id, ct);
            sentences += coverage.Sentences;
            fromSubtitles += coverage.FromSubtitles;
            cachedAi += coverage.CachedAi;
            needAi += coverage.NeedAi.Count;
        }

        var active = await translations.ActiveProviderAsync(ct);
        var model = active?.Config.Get("model");
        var price = active != null && model != null ? active.Value.Provider.PriceFor(model) : null;
        decimal? cost = price == null ? null : needAi * (EstimatedInputTokens * price.Input + EstimatedOutputTokens * price.Output) / 1_000_000m;

        return Ok(new
        {
            sentences, fromSubtitles, cachedAi, needAi,
            provider = active?.Provider.Name, model, estimatedCostUsd = cost,
            progress = cache.Get<TranslationJobProgress>(SentenceTranslationJob.ProgressKey(deckId))
        });
    }

    [HttpPost("titles/{deckId:int}/translate")]
    public async Task<IActionResult> TranslateTitle(int deckId, CancellationToken ct)
    {
        if (await translations.ActiveProviderAsync(ct) == null) return BadRequest(new { message = "Choose a provider and save its API key first." });
        if (cache.Get<TranslationJobProgress>(SentenceTranslationJob.ProgressKey(deckId)) is { Running: true })
            return Conflict(new { message = "This title is already being translated." });

        cache.Set(SentenceTranslationJob.ProgressKey(deckId), new TranslationJobProgress(0, 0, 0, null, true, DateTime.UtcNow), TimeSpan.FromDays(1));
        backgroundJobs.Enqueue<SentenceTranslationJob>(job => job.TranslateTitle(deckId, CancellationToken.None));
        return Accepted();
    }

    private async Task<List<int>> DeckAndChildren(int deckId, CancellationToken ct) =>
        await context.Decks.AsNoTracking().Where(d => d.DeckId == deckId || d.ParentDeckId == deckId).Select(d => d.DeckId).ToListAsync(ct);

    private SettingGroupDto Group(Dictionary<string, Dictionary<string, string>> group, string id, string name, IReadOnlyList<SettingField> fields)
    {
        var config = settingsStore.Config(group, id, fields);
        var saved = group.GetValueOrDefault(id) ?? new Dictionary<string, string>();
        return new SettingGroupDto(id, name, fields.Select(f =>
        {
            var isSet = saved.ContainsKey(f.Key);
            var secret = f.Kind == SettingFieldKind.Secret;
            var value = config.Get(f.Key);
            return new SettingFieldDto(f.Key, f.Label, f.Kind.ToString().ToLowerInvariant(), f.Default, f.Options, f.Help,
                                       secret ? null : value, isSet, secret && isSet ? (f.ShowHint ? TranslationSettingsStore.Hint(value) : "set") : null);
        }).ToList());
    }
}
