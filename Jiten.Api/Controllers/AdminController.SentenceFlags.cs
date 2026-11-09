using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.User;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>Flagged public lines, one per line, most flagged first. <paramref name="reason"/> keeps lines with a flag giving it.</summary>
    [HttpGet("sentence-flags")]
    public async Task<IResult> GetSentenceFlags([FromServices] SentenceFlagService flags, [FromQuery] string? reason, CancellationToken ct,
                                                [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        if (reason != null && !SentenceFlagReason.IsValid(reason)) return Results.BadRequest(new { message = "Unknown reason." });
        return Results.Ok(await flags.AdminListAsync(reason, Math.Max(offset, 0), Math.Clamp(limit, 1, 200), ct));
    }

    /// <summary>Closes every open flag on a line as fixed, so it comes back for everyone who flagged it.</summary>
    [HttpPost("sentence-flags/{sentenceId:long}/fixed")]
    public async Task<IResult> MarkSentenceFixed([FromServices] SentenceFlagService flags, long sentenceId, CancellationToken ct)
    {
        var closed = await flags.MarkFixedAsync(sentenceId, ct);
        if (closed == 0) return Results.NotFound(new { message = "This line has no open flags." });
        logger.LogInformation("Admin marked sentence {SentenceId} fixed, closing {Count} flags", sentenceId, closed);
        return Results.Ok(new { closed });
    }

    /// <summary>Titles with a submitted text fix their last parse hasn't seen.</summary>
    [HttpGet("sentence-flags/corrected-titles")]
    public async Task<IResult> GetCorrectedTitles([FromServices] ParseCorrectionService corrections, CancellationToken ct) =>
        Results.Ok(await corrections.CorrectedTitlesAsync(ct));

    /// <summary>Reparses each corrected title whole, so its fixes reach its word lists and stats.</summary>
    [HttpPost("sentence-flags/reparse-corrected")]
    public async Task<IResult> ReparseCorrectedTitles([FromServices] ParseCorrectionService corrections, CancellationToken ct)
    {
        var titles = await corrections.CorrectedTitlesAsync(ct);
        foreach (var title in titles)
            backgroundJobs.Enqueue<ReparseJob>(job => job.Reparse(title.DeckId, false));
        logger.LogInformation("Admin queued reparses of {Count} corrected titles", titles.Count);
        return Results.Ok(new { queued = titles.Count });
    }

    /// <summary>Closes every open flag on a line as "not a problem": nothing was wrong, and the line comes back for everyone who flagged it.</summary>
    [HttpPost("sentence-flags/{sentenceId:long}/reject")]
    public async Task<IResult> RejectSentenceFlags([FromServices] SentenceFlagService flags, long sentenceId, CancellationToken ct)
    {
        var closed = await flags.RejectAsync(sentenceId, ct);
        if (closed == 0) return Results.NotFound(new { message = "This line has no open flags." });
        logger.LogInformation("Admin found nothing wrong with sentence {SentenceId}, closing {Count} flags", sentenceId, closed);
        return Results.Ok(new { closed });
    }
}
