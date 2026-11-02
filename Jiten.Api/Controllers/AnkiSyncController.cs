using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>How Sync with Anki treats each note type, and where new mined cards go. The AnkiConnect side runs in the browser.</summary>
[ApiController]
[Route("api/anki-sync")]
[Authorize]
[EnableRateLimiting("fixed")]
public class AnkiSyncController(AnkiSyncSetupService setups, ICurrentUserService userService) : ControllerBase
{
    [HttpGet("setups")]
    [SwaggerOperation(Summary = "The user's note type setups, and when known words were last sent")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await setups.ListAsync(userId, ct));
    }

    [HttpPut("setups")]
    [SwaggerOperation(Summary = "Creates or replaces the setup of one note type")]
    public async Task<IActionResult> Save([FromBody] AnkiNoteTypeSetupDto setup, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (AnkiSyncSetupService.Validate(setup) is { } error) return BadRequest(new { message = error });
        try
        {
            var saved = await setups.SaveAsync(userId, setup, ct);
            return Ok(new { setup = saved, knownWordsSyncedAt = (await setups.ListAsync(userId, ct)).KnownWordsSyncedAt });
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpDelete("setups")]
    [SwaggerOperation(Summary = "Stops syncing a note type; words it made known stay known")]
    // A query value, since note type names can hold a slash, which a route segment doesn't decode.
    public async Task<IActionResult> Delete([FromQuery] string modelName, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await setups.DeleteAsync(userId, modelName, ct) ? NoContent() : NotFound();
    }

    [HttpGet("card-target")]
    [SwaggerOperation(Summary = "The deck, note type, tags and audio padding new mined cards get")]
    public async Task<IActionResult> CardTarget(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await setups.CardTargetAsync(userId, ct));
    }

    [HttpPut("card-target")]
    [SwaggerOperation(Summary = "Sets where new mined cards go")]
    public async Task<IActionResult> SaveCardTarget([FromBody] AnkiCardTargetDto target, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        await setups.SaveCardTargetAsync(userId, target, ct);
        return NoContent();
    }

    [HttpPut("known-words-synced")]
    [SwaggerOperation(Summary = "Records when Sync last sent known words, so cards unchanged since aren't sent again; Previous is the value Sync started from")]
    public async Task<IActionResult> KnownWordsSynced([FromBody] KnownWordsSyncedRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        // Only when nothing reset it since the Sync read it, so a setup changed meanwhile still gets every card next time.
        return await setups.SetKnownWordsSyncedAsync(userId, request.SyncedAt, request.Previous, ct)
            ? NoContent()
            : Conflict(new { message = "A setup changed during Sync; known words will all be sent next time." });
    }
}
