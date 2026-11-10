using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Api.Services.EpisodeImport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

public record EpisodeImportStarted(string RunId);

/// <param name="IPlusOne">Per episode deck, the i+1 sentences for the admin looking, once that episode is done.</param>
public record EpisodeImportStatus(EpisodeImportRun Run, Dictionary<int, int> IPlusOne);

/// <summary>Imports episodes of a title from local video: browse the media roots, scan videos, run the import, follow it.</summary>
[ApiController]
[Route("api/admin")]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize("RequiresAdmin")]
public class AdminEpisodeImportController(
    EpisodeImportService service,
    EpisodeImportTracker tracker,
    IBackgroundJobClient backgroundJobs,
    ISentenceStatsService sentenceStats,
    ILogger<AdminEpisodeImportController> logger) : ControllerBase
{
    [HttpGet("media-files")]
    public IActionResult ListFolder([FromQuery] string? path)
    {
        try
        {
            return Ok(service.ListFolder(path));
        }
        catch (EpisodeImportException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("decks/{parentId:int}/episode-import/scan")]
    public async Task<IActionResult> Scan(int parentId, [FromBody] EpisodeScanRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await service.ScanAsync(parentId, request, ct));
        }
        catch (EpisodeImportException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("decks/{parentId:int}/episode-import")]
    public IActionResult Import(int parentId, [FromBody] EpisodeImportRequest request)
    {
        if (request.Items.Count == 0) return BadRequest(new { message = "Pick at least one episode." });
        if (request.Items.Any(i => i.Episode <= 0)) return BadRequest(new { message = "Every episode needs a number." });
        if (request.Items.GroupBy(i => i.Episode).Any(g => g.Count() > 1)) return BadRequest(new { message = "Two rows are the same episode." });

        // Two runs on one title would rebuild its word list over each other
        if (tracker.Latest(parentId) is { Finished: false })
            return Conflict(new { message = "An import for this title is still running; wait for it to finish." });

        var items = request.Items.OrderBy(i => i.Episode).ToList();
        var run = tracker.Start(parentId, items.Select(i => (i.Episode, Path.GetFileName(i.VideoPath))));
        backgroundJobs.Enqueue<EpisodeImportJob>(job => job.Run(run.Id, parentId, items));
        logger.LogInformation("Admin queued episode import {RunId} for deck {ParentId}: episodes {Episodes}", run.Id, parentId,
                              string.Join(",", items.Select(i => i.Episode)));
        return Ok(new EpisodeImportStarted(run.Id));
    }

    [HttpGet("decks/{parentId:int}/episode-import/latest")]
    public async Task<IActionResult> Latest(int parentId) =>
        tracker.Latest(parentId) is { } run ? Ok(await StatusAsync(run)) : NoContent();

    [HttpGet("episode-import/{runId}")]
    public async Task<IActionResult> Status(string runId) =>
        tracker.Get(runId) is { } run ? Ok(await StatusAsync(run)) : NotFound(new { message = "That import isn't known; the API may have restarted." });

    private async Task<EpisodeImportStatus> StatusAsync(EpisodeImportRun run)
    {
        // Counted only once the whole run is done: the title's totals and the stats cache settle then
        var iPlusOne = new Dictionary<int, int>();
        if (run.Finished)
            foreach (var report in run.Episodes.Select(e => e.Report).OfType<EpisodeImportReport>())
                if (await sentenceStats.GetIPlusOneHitsAsync(report.DeckId) is { } listing)
                    iPlusOne[report.DeckId] = listing.Hits.Count;
        return new EpisodeImportStatus(run, iPlusOne);
    }
}
