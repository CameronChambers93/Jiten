using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>The user's sentence pipelines (n1miner): what the sentence button does on each page. The browser runs them.</summary>
[ApiController]
[Route("api/mining/pipelines")]
[Authorize]
[EnableRateLimiting("fixed")]
public class MiningPipelinesController(MiningPipelineService pipelines, ICurrentUserService userService) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "The user's pipelines and each page's default; before the first save, built from the card settings")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await pipelines.GetAsync(userId, ct));
    }

    [HttpPut]
    [SwaggerOperation(Summary = "Replaces the user's pipelines and defaults")]
    public async Task<IActionResult> Save([FromBody] MiningPipelinesDto body, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (MiningPipelineService.Validate(body) is { } error) return BadRequest(new { message = error });
        return Ok(await pipelines.SaveAsync(userId, body, ct));
    }
}
