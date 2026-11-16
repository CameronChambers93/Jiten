using Jiten.Api.Services;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

/// <summary>
/// Bulk sentence checks (GDR-060): Claude Code sessions lease batches of deck lines and post verdicts; admins review what
/// was flagged. Nothing about a line changes until an admin accepts a suggestion.
/// </summary>
[ApiController]
[Route("api/admin/sentence-checks")]
[Authorize("RequiresAdmin")]
[EnableRateLimiting("fixed")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AdminSentenceCheckController(SentenceCheckService checks, ICurrentUserService userService) : ControllerBase
{
    /// <summary>Leases up to <paramref name="size"/> lines to check for 30 minutes, grouped by identical text and English.</summary>
    /// <param name="deckIds">Titles (parent decks) to take lines from; none means every title.</param>
    [HttpGet("next")]
    public async Task<CheckBatch> Next([FromQuery] List<int>? deckIds, [FromQuery] int size = 50, CancellationToken ct = default) =>
        await checks.NextAsync(deckIds, size, ct);

    [HttpPost]
    public async Task<IActionResult> Results([FromBody] CheckResultsRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await checks.PostResultsAsync(request, ct));
        }
        catch (SentenceCheckException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Queue([FromQuery] string status = SentenceCheckStatus.Flagged, [FromQuery] string? kind = null,
                                           [FromQuery] int? deckId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50,
                                           CancellationToken ct = default)
    {
        if (kind != null && !SentenceCheckKind.All.Contains(kind)) return BadRequest(new { message = $"Unknown kind \"{kind}\"." });
        var (items, total) = await checks.QueueAsync(status, kind, deckId, Math.Max(0, offset), limit, ct);
        return Ok(new { items, total });
    }

    [HttpPost("{sentenceId:long}/accept")]
    public async Task<IActionResult> Accept(long sentenceId, [FromBody] AcceptCheckRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return await checks.AcceptAsync(sentenceId, userId, request, ct) ? NoContent() : NotFound();
        }
        catch (Exception e) when (e is SentenceCheckException or SentenceFixException)
        {
            return Conflict(new { message = e.Message });
        }
    }

    [HttpPost("{sentenceId:long}/dismiss")]
    public async Task<IActionResult> Dismiss(long sentenceId, [FromBody] DismissCheckRequest? request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await checks.DismissAsync(sentenceId, userId, request?.Note, ct) ? NoContent() : NotFound();
    }

    /// <param name="deckIds">Titles to count; none means every title with lines.</param>
    [HttpGet("progress")]
    public async Task<List<CheckProgress>> Progress([FromQuery] List<int>? deckIds, CancellationToken ct) =>
        await checks.ProgressAsync(deckIds, ct);
}
