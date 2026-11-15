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
public class MiningController(MiningSentenceService mining, ICurrentUserService userService) : ControllerBase
{
    [HttpGet("options")]
    [SwaggerOperation(Summary = "Titles (with episodes) that have sentences, and the imported frequency dictionaries")]
    public Task<MineOptionsDto> GetOptions(CancellationToken ct) => mining.OptionsAsync(ct);

    [HttpPost("sentences")]
    [SwaggerOperation(Summary = "A page of i+1 sentences across the chosen titles, grouped by word or one per row")]
    public async Task<ActionResult<MineResponse>> Search([FromBody] MineRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.ExcludeTerms is { Count: > 100_000 }) return BadRequest(new { message = "Too many words to exclude." });
        if (request.IncludeTerms is { Count: > 100_000 }) return BadRequest(new { message = "Too many words in the deck." });
        int?[] counts = [request.UnlearnedMin, request.UnlearnedMax, request.LearningMin, request.LearningMax];
        if (counts.Any(c => c is < 0 or > MineRequest.MaxCount))
            return BadRequest(new { message = $"Word counts go from 0 to {MineRequest.MaxCount}." });
        return Ok(await mining.SearchAsync(userId, request, ct));
    }
}
