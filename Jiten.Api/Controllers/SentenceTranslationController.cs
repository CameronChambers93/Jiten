using Jiten.Api.Services.Translation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public record SentenceTranslationOptionsDto(string CardMode, string PageMode, bool PageAutoAi, bool AiAvailable);

public record SentenceTranslationsRequest(List<long> SentenceIds);

/// <summary>
/// English for example sentences, from English subtitles or an AI provider. AI calls cost the site owner money, so only
/// administrators can trigger new ones; everyone gets subtitle and already cached translations.
/// </summary>
[ApiController]
[Route("api/sentence-translation")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceTranslationController(SentenceTranslationService translations, TranslationSettingsStore settingsStore) : ControllerBase
{
    private const int MaxBatch = 100;

    [HttpGet("options")]
    [SwaggerOperation(Summary = "When the site shows English and puts it on cards")]
    public async Task<SentenceTranslationOptionsDto> GetOptions(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var aiAvailable = CanUseAi && await translations.ActiveProviderAsync(ct) != null;
        return new SentenceTranslationOptionsDto(settings.CardMode, settings.PageMode, settings.PageAutoAi, aiAvailable);
    }

    [HttpGet("{sentenceId:long}")]
    [SwaggerOperation(Summary = "A sentence's English; with ai=true, translates it with the AI provider when there's none yet")]
    public async Task<IActionResult> Get(long sentenceId, [FromQuery] bool ai = false, CancellationToken ct = default)
    {
        try
        {
            var translation = await translations.GetAsync(sentenceId, ai && CanUseAi, ct);
            return Ok(new { translation });
        }
        catch (TranslationProviderException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = e.Message });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("batch")]
    [SwaggerOperation(Summary = "Subtitle and cached English for many sentences (no AI calls)")]
    public async Task<IActionResult> GetMany([FromBody] SentenceTranslationsRequest request, CancellationToken ct)
    {
        if (request.SentenceIds.Count > MaxBatch) return BadRequest(new { message = $"At most {MaxBatch} sentences per request" });
        return Ok(new { translations = await translations.GetManyAsync(request.SentenceIds.Distinct().ToList(), ct) });
    }

    private bool CanUseAi => User.IsInRole("Administrator");
}
