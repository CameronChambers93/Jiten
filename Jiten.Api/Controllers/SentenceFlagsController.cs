using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>The user's flagged sentences, each hidden for them until it's fixed or dismissed.</summary>
[ApiController]
[Route("api/sentence-flags")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceFlagsController(SentenceFlagService flags, ICurrentUserService userService) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "The user's open sentence flags, oldest first")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { flags = await flags.ListAsync(userId, ct) });
    }

    [HttpPost]
    [SwaggerOperation(Summary = "Flags a sentence, or updates the reason and note of its open flag")]
    public async Task<IActionResult> Save([FromBody] SaveSentenceFlagRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (!SentenceFlagReason.IsValid(request.Reason)) return BadRequest(new { message = "Choose a reason." });
        if (request.Note?.Length > SentenceFlagService.MaxNoteLength) return BadRequest(new { message = $"Notes are at most {SentenceFlagService.MaxNoteLength} characters." });
        return await flags.SaveAsync(userId, request, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{sentenceFlagId:long}/dismiss")]
    [SwaggerOperation(Summary = "Closes a flag without a fix, which brings the sentence back")]
    public async Task<IActionResult> Dismiss(long sentenceFlagId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await flags.DismissAsync(userId, sentenceFlagId, ct) ? NoContent() : NotFound();
    }
}
