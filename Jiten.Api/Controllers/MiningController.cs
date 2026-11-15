using Jiten.Api.Authorization;
using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>The Mine page: the user's i+1 sentences across titles, sorted and filtered.</summary>
[ApiController]
[Route("api/mining")]
[Authorize]
[JitenPlus(Feature = "sentence-stats")]
[EnableRateLimiting("fixed")]
public class MiningController(MiningSentenceService mining, MineSearchService searches, ICurrentUserService userService) : ControllerBase
{
    [HttpGet("options")]
    [SwaggerOperation(Summary = "Titles (with episodes) that have sentences, and the imported frequency dictionaries")]
    public Task<MineOptionsDto> GetOptions(CancellationToken ct) => mining.OptionsAsync(ct);

    [HttpPost("sentences")]
    [SwaggerOperation(Summary = "A page of i+1 sentences across the chosen titles, grouped by word or one per row")]
    public async Task<ActionResult<MineResponse>> Search([FromBody] MineRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (Invalid(request) is { } problem) return BadRequest(new { message = problem });
        return Ok(await mining.SearchAsync(userId, request, ct));
    }

    [HttpPost("sentences/all")]
    [SwaggerOperation(Summary = "The plan of a bulk run: every word of the search, up to 500, with its top sentences (GDR-059)")]
    public async Task<ActionResult<MineRunPlanDto>> All([FromBody] MineRunPlanRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.Search == null) return BadRequest(new { message = "A search is needed." });
        if (Invalid(request.Search) is { } problem) return BadRequest(new { message = problem });
        if (request.PerWord is < 1 or > MiningSentenceService.SentencesPerWord)
            return BadRequest(new { message = $"Sentences per word go from 1 to {MiningSentenceService.SentencesPerWord}." });
        return Ok(await mining.AllAsync(userId, request.Search, request.PerWord, ct));
    }

    [HttpGet("searches")]
    [SwaggerOperation(Summary = "The user's saved Mine searches")]
    public async Task<ActionResult<List<MineSearchDto>>> GetSearches(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await searches.GetAsync(userId, ct));
    }

    [HttpPut("searches")]
    [SwaggerOperation(Summary = "Replaces the user's saved Mine searches")]
    public async Task<ActionResult<List<MineSearchDto>>> SaveSearches([FromBody] List<MineSearchDto> request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return Ok(await searches.SaveAsync(userId, request, ct));
        }
        catch (MineSearchException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    private static string? Invalid(MineRequest request)
    {
        if (request.ExcludeTerms is { Count: > 100_000 }) return "Too many words to exclude.";
        if (request.IncludeTerms is { Count: > 100_000 }) return "Too many words in the deck.";
        int?[] counts = [request.UnlearnedMin, request.UnlearnedMax, request.LearningMin, request.LearningMax];
        if (counts.Any(c => c is < 0 or > MineRequest.MaxCount)) return $"Word counts go from 0 to {MineRequest.MaxCount}.";
        return null;
    }
}
