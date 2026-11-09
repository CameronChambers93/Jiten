using Jiten.Api.Services;
using Jiten.Core.Data.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Fixing a sentence: the user's own version of its text, English, clip and frame, and submitting it to the shared line
/// (an admin's at once, anyone else's for review).
/// </summary>
[ApiController]
[Route("api/sentences/{sentenceId:long}/fix")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceFixController(SentenceFixService fixes, SentenceSubmissionService submissions, ICurrentUserService userService) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "The fix screen's data: the original, the shared fix and the caller's, the nearby cues, same-text lines and mined sentences")]
    public async Task<IActionResult> Get(long sentenceId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await fixes.EditorAsync(userId, sentenceId, ct) is { } editor
            ? Ok(editor with { Submission = await submissions.StateAsync(userId, sentenceId, ct) })
            : NotFound();
    }

    [HttpPut]
    [SwaggerOperation(Summary = "Saves the caller's fix of the line (all parts null resets it) and closes their open flag on it")]
    public async Task<IActionResult> Save(long sentenceId, [FromBody] SaveSentenceFixRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return await fixes.SaveAsync(userId, sentenceId, request, ct) ? NoContent() : NotFound();
        }
        catch (SentenceFixException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpDelete]
    [SwaggerOperation(Summary = "Resets the caller's fix of the line")]
    public async Task<IActionResult> Reset(long sentenceId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await fixes.SaveAsync(userId, sentenceId, new SaveSentenceFixRequest(null, null, null, null, null), ct) ? NoContent() : NotFound();
    }

    [HttpPost("submit")]
    [SwaggerOperation(Summary = "Submits the caller's fix: an admin's becomes the shared line at once, anyone else's waits for review. Ticked same-text lines get the fixed text too.")]
    public async Task<IActionResult> Submit(long sentenceId, [FromBody] SubmitSentenceFixRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        var admin = User.IsInRole(nameof(UserRole.Administrator));
        try
        {
            var done = admin
                ? await fixes.SubmitAsync(userId, sentenceId, request, ct)
                : await submissions.SubmitAsync(userId, sentenceId, request, ct);
            return done ? Ok(new { applied = admin }) : NotFound();
        }
        catch (SentenceFixException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpDelete("submission")]
    [SwaggerOperation(Summary = "Withdraws the caller's submission of the line that's waiting for review")]
    public async Task<IActionResult> Withdraw(long sentenceId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await submissions.WithdrawAsync(userId, sentenceId, ct) ? NoContent() : NotFound();
    }

    [HttpPost("approved")]
    [SwaggerOperation(Summary = "Answers \"a fix was approved for a line you fixed\": use the approved parts, or keep the caller's own")]
    public async Task<IActionResult> AnswerApproved(long sentenceId, [FromBody] AnswerApprovedRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await fixes.AnswerApprovedAsync(userId, sentenceId, request.Use, ct) ? NoContent() : NotFound();
    }

    [HttpGet("english")]
    [SwaggerOperation(Summary = "The subtitle English for a clip of the line's episode, in subtitle time")]
    public async Task<IActionResult> English(long sentenceId, [FromQuery] int startMs, [FromQuery] int endMs, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (endMs <= startMs || endMs - startMs > SentenceFixService.MaxClipMs) return BadRequest(new { message = "Not a valid clip." });
        return await fixes.SuggestEnglishAsync(userId, sentenceId, startMs, endMs, ct) is var (found, text) && found ? Ok(new { text }) : NotFound();
    }
}

public record AnswerApprovedRequest(bool Use);
