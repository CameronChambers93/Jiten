using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>A sentence's own page.</summary>
[ApiController]
[Route("api/sentences")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceController(SentenceDetailService details) : ControllerBase
{
    [HttpGet("{sentenceId:long}")]
    [SwaggerOperation(Summary = "A sentence with its words, surrounding subtitle lines and other occurrences")]
    public async Task<ActionResult<SentenceDetailDto>> Get(long sentenceId, [FromQuery] int wordId = 0, [FromQuery] int readingIndex = 0, CancellationToken ct = default)
    {
        var detail = await details.GetAsync(sentenceId, wordId, readingIndex, ct);
        return detail == null ? NotFound() : Ok(detail);
    }
}
