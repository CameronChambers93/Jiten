using Jiten.Api.Services;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.User;
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
    private const int MaxWordSummaries = 100;

    [HttpGet("settings")]
    [SwaggerOperation(Summary = "The user's sentence store settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { targetWordTemplate = await sync.TargetTemplateAsync(userId, ct) });
    }

    [HttpPut("settings")]
    [SwaggerOperation(Summary = "Sets how cards mark the target word: a template holding {word} once")]
    public async Task<IActionResult> SaveSettings([FromBody] MinedSettingsRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (!TargetWordMarkup.IsValid(request.TargetWordTemplate))
            return BadRequest(new { message = $"The template must hold {TargetWordMarkup.Placeholder} once, in at most {TargetWordMarkup.MaxTemplateLength} characters." });
        await sync.SaveTargetTemplateAsync(userId, request.TargetWordTemplate, ct);
        return NoContent();
    }

    [HttpGet("sync/folds")]
    [SwaggerOperation(Summary = "Words on more than one Anki note to fold, and folds recorded but not yet confirmed")]
    public async Task<IActionResult> Folds(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await sync.FoldsAsync(userId, ct));
    }

    [HttpPost("sync/fold")]
    [SwaggerOperation(Summary = "Records a fold the user confirmed, before the browser deletes the folded notes in Anki")]
    public async Task<IActionResult> Fold([FromBody] FoldRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await sync.BeginFoldAsync(userId, request, ct)
            ? NoContent()
            : BadRequest(new { message = "Those notes aren't this word's, or a fold of this word is still unfinished: Sync first." });
    }

    [HttpPost("sync/fold/{minedWordId:long}/confirm")]
    [SwaggerOperation(Summary = "Completes a fold once its notes are deleted in Anki (or only the ones named gone); returns what to write to the kept note")]
    public async Task<IActionResult> ConfirmFold(long minedWordId, [FromBody] ConfirmFoldRequest? request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        var (confirmed, write) = await sync.ConfirmFoldAsync(userId, minedWordId, request?.GoneAnkiNoteIds, ct);
        return confirmed ? Ok(new { write }) : NotFound();
    }

    [HttpDelete("sync/fold/{minedWordId:long}")]
    [SwaggerOperation(Summary = "Drops a pending fold whose notes are still in Anki")]
    public async Task<IActionResult> CancelFold(long minedWordId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await sync.CancelFoldAsync(userId, minedWordId, ct) ? NoContent() : NotFound();
    }

    [HttpPost("sync/flags")]
    [SwaggerOperation(Summary = "Marks sentences flagged from Anki cards; returns the flags that matched, whose tags can then be removed")]
    public async Task<IActionResult> Flags([FromBody] FlagsRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.Flags.Count > MaxFlags) return BadRequest(new { message = $"At most {MaxFlags} flags per request." });
        return Ok(new { applied = await sync.ApplyFlagsAsync(userId, request.Flags, ct) });
    }

    [HttpGet("sync/notes")]
    [SwaggerOperation(Summary = "What to write to each word's kept Anki note: its sentence list and, when they changed, its main fields")]
    public async Task<IActionResult> NoteWrites(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new { notes = await sync.NoteWritesAsync(userId, null, ct) });
    }

    [HttpPost("sync/main-written")]
    [SwaggerOperation(Summary = "Records the main fields the browser wrote to notes")]
    public async Task<IActionResult> MainWritten([FromBody] MainWrittenRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        if (request.Notes.Count > MaxFlags) return BadRequest(new { message = $"At most {MaxFlags} notes per request." });
        await sync.MainWrittenAsync(userId, request.Notes, ct);
        return NoContent();
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

    [HttpPost("sentences")]
    [SwaggerOperation(Summary = "Adds an example sentence to the word of an Anki note already in the store; returns what to write to its kept note")]
    public async Task<IActionResult> AddSentence([FromBody] AddSentenceRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            var result = await mined.AddToNoteAsync(userId, request, ct);
            return result == null ? NotFound(new { message = "That Anki note isn't in the sentence store yet." }) : Ok(result);
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpGet("word-summaries")]
    [SwaggerOperation(Summary = "Written form, furigana and first meaning of words picked out of a sentence (wordId:readingIndex each)")]
    public async Task<IActionResult> WordSummaries([FromQuery] List<string> words, CancellationToken ct)
    {
        var parsed = new List<(int, byte)>();
        foreach (var w in words.Take(MaxWordSummaries))
        {
            var parts = w.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var wordId) || !byte.TryParse(parts[1], out var readingIndex))
                return BadRequest(new { message = $"\"{w}\" isn't wordId:readingIndex." });
            parsed.Add((wordId, readingIndex));
        }

        return Ok(new { words = parsed.Count == 0 ? [] : await mined.WordSummariesAsync(parsed, ct) });
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
