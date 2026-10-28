using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>The user's mined words and their sentences (n1miner's sentence store), imported from their Anki notes.</summary>
[ApiController]
[Route("api/mined")]
[Authorize]
[EnableRateLimiting("fixed")]
public class MinedWordsController(MinedSentenceService mined, MinedSyncService sync, ICurrentUserService userService) : ControllerBase
{
    private const int MaxFlags = 1000;

    [HttpGet("sync/folds")]
    [SwaggerOperation(Summary = "Words on more than one Anki note, with the note to keep and the ones to fold into it")]
    public async Task<IActionResult> Folds(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { candidates = await sync.FoldCandidatesAsync(userId, ct) });
    }

    [HttpPost("sync/fold")]
    [SwaggerOperation(Summary = "Records a confirmed fold, after the browser deleted the folded notes in Anki")]
    public async Task<IActionResult> Fold([FromBody] FoldRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await sync.FoldAsync(userId, request, ct) ? NoContent() : BadRequest(new { message = "Those notes aren't this word's." });
    }

    [HttpPost("sync/flags")]
    [SwaggerOperation(Summary = "Marks sentences flagged from Anki cards; returns the flags that matched, whose tags can then be removed")]
    public async Task<IActionResult> Flags([FromBody] FlagsRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.Flags.Count > MaxFlags) return BadRequest(new { message = $"At most {MaxFlags} flags per request." });
        return Ok(new { applied = await sync.ApplyFlagsAsync(userId, request.Flags, ct) });
    }

    [HttpGet("sync/sentences")]
    [SwaggerOperation(Summary = "The Sentences field to write to each word's Anki note")]
    public async Task<IActionResult> NoteSentences(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { notes = await sync.NoteSentencesAsync(userId, ct) });
    }

    [HttpPost("import")]
    // Media travels as base64 in the JSON; the page sends small batches.
    [RequestSizeLimit(64 * 1024 * 1024)]
    [SwaggerOperation(Summary = "Imports Anki notes (read by the browser through AnkiConnect) into the sentence store; notes imported before are skipped")]
    public async Task<IActionResult> Import([FromBody] ImportNotesRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.Notes.Count is 0 or > MinedSentenceService.MaxNotesPerImport)
            return BadRequest(new { message = $"Send 1 to {MinedSentenceService.MaxNotesPerImport} notes per request." });
        return Ok(new { results = await mined.ImportAsync(userId, request.Notes, ct) });
    }

    [HttpGet("notes")]
    [SwaggerOperation(Summary = "The Anki note ids already in the store, so the browser only reads new notes")]
    public async Task<IActionResult> NoteIds(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { ankiNoteIds = await mined.NoteIdsAsync(userId, ct) });
    }

    [HttpGet("words")]
    [SwaggerOperation(Summary = "A page of mined words, most recently changed first")]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 0, CancellationToken ct = default)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await mined.ListAsync(userId, search, page, ct));
    }

    [HttpGet("words/{minedWordId:long}")]
    [SwaggerOperation(Summary = "A mined word with its Anki notes and sentences")]
    public async Task<IActionResult> Get(long minedWordId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await mined.GetAsync(userId, minedWordId, ct) is { } word ? Ok(word) : NotFound();
    }
}
