using Microsoft.EntityFrameworkCore;
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
public class MinedWordsController(MinedSentenceService mined, MinedSyncService sync, WordMatchService wordMatch, ICurrentUserService userService,
                                  IExampleSentenceQueryService exampleSentences)
    : ControllerBase
{
    private const int MaxFlags = 1000;
    private const int MaxMatches = 500;
    private const int MaxWordSummaries = 100;
    private const int MaxListStates = 200;

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

    [HttpPost("list")]
    [SwaggerOperation(Summary = "Saves one of Jiten's lines to a word's list, kept for Jiten only; a line the word has already is left as it is")]
    public async Task<IActionResult> SaveToList([FromBody] SaveToListRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return Ok(await mined.SaveToListAsync(userId, request, ct));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpGet("list/state")]
    [SwaggerOperation(Summary = "Which of these lines (wordId:readingIndex:sentenceId each) are in the word's list, and whether on its Anki card")]
    public async Task<IActionResult> ListStates([FromQuery] List<string> lines, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        var parsed = lines.SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries))
                          .Select(l => l.Split(':'))
                          .Where(p => p.Length == 3 && int.TryParse(p[0], out _) && byte.TryParse(p[1], out _) && long.TryParse(p[2], out _))
                          .Select(p => (int.Parse(p[0]), byte.Parse(p[1]), long.Parse(p[2])))
                          .Distinct().ToList();
        if (parsed.Count > MaxListStates) return BadRequest(new { message = $"At most {MaxListStates} lines per request." });
        return Ok(parsed.Count == 0 ? new List<ListStateDto>() : await mined.ListStatesAsync(userId, parsed, ct));
    }

    [HttpGet("rebuild")]
    [SwaggerOperation(Summary = "The caller's mined sentences whose line changed (a re-ingest as whole-cue lines), each with its line and word marked")]
    public async Task<IActionResult> SentencesToRebuild(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        var sentences = await mined.SentencesToRebuildAsync(userId, ct);

        // One query per word: the line with that word marked, else the stored place of the word
        foreach (var group in sentences.GroupBy(s => (WordId: s.WordId ?? 0, ReadingIndex: s.ReadingIndex ?? 0)))
        {
            var dtos = (await exampleSentences.BuildDtosAsync(group.Select(s => s.ExampleSentenceId).Distinct().ToList(),
                                                              group.Key.WordId, group.Key.ReadingIndex))
                .ToDictionary(d => d.SentenceId);
            foreach (var s in group)
            {
                var dto = dtos.GetValueOrDefault(s.ExampleSentenceId);
                // The stored place is the occurrence that was mined, when a line holds the word twice. It's a place in the line's
                // text, so not in the user's own fixed text, where the word is found from that text's tokens instead.
                if (dto is { Fixed: not "mine" } && s.TargetPosition is { } position && s.TargetLength is { } length && position + length <= dto.Text.Length)
                    dto = dto.ForWord(position, length);
                s.Sentence = dto;
            }
        }

        // A line that can't be shown any more won't become buildable: stop asking
        var gone = sentences.Where(s => s.Sentence == null).Select(s => s.MinedSentenceId).ToList();
        if (gone.Count > 0) await mined.ClearRebuildAsync(userId, gone, ct);

        return Ok(sentences.Where(s => s.Sentence != null));
    }

    [HttpPost("sentences/{minedSentenceId:long}/rebuild")]
    [SwaggerOperation(Summary = "Stores a mined sentence's text, furigana and English rebuilt from its line")]
    public async Task<IActionResult> Rebuild(long minedSentenceId, [FromBody] RebuildSentenceRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return await mined.RebuildAsync(userId, minedSentenceId, request, ct) ? NoContent() : NotFound();
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpPost("notes/created")]
    [SwaggerOperation(Summary = "Records a note just created in Anki for an example sentence; returns what to write to the word's kept note")]
    public async Task<IActionResult> NoteCreated([FromBody] CreatedNoteRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return Ok(await mined.RecordCreatedAsync(userId, request, ct));
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
        catch (DbUpdateException)
        {
            // Recorded at the same moment by another request or a Sync.
            return Conflict(new { message = "The note was recorded at the same time by another request." });
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
    [SwaggerOperation(Summary = "The Anki note ids already in the store, so the browser only reads new notes; those without a sentence of their own; and those whose word was never checked")]
    public async Task<IActionResult> NoteIds(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(new
        {
            ankiNoteIds = await mined.NoteIdsAsync(userId, ct), withoutSentence = await mined.NoteIdsWithoutSentenceAsync(userId, ct),
            @unchecked = await mined.UncheckedNoteIdsAsync(userId, ct)
        });
    }

    [HttpGet("notes/{ankiNoteId:long}/candidates")]
    [SwaggerOperation(Summary = "The dictionary entries a stored note's word could be, with their glosses, and the one it's on")]
    public async Task<IActionResult> NoteCandidates(long ankiNoteId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await mined.NoteCandidatesAsync(userId, ankiNoteId, ct) is { } candidates ? Ok(candidates) : NotFound();
    }

    [HttpPost("notes/{ankiNoteId:long}/word")]
    [SwaggerOperation(Summary = "Puts a stored note on the entry picked, or confirms the one it's on; it moves with its sentences")]
    public async Task<IActionResult> SetNoteWord(long ankiNoteId, [FromBody] SetNoteWordRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return await mined.SetNoteWordAsync(userId, ankiNoteId, request.WordId, request.ReadingIndex, ct) is { } result ? Ok(result) : NotFound();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpPost("match")]
    [SwaggerOperation(Summary = "Which dictionary entry each Anki word is, decided by the note's text when several share the word and reading")]
    public async Task<IActionResult> Match([FromBody] WordMatchBatchRequest request, CancellationToken ct)
    {
        if (userService.UserId is null) return Unauthorized();
        if (request.Words.Count > MaxMatches) return BadRequest(new { message = $"At most {MaxMatches} words at a time." });
        return Ok(new { matches = await wordMatch.MatchAsync(request.Words, ct) });
    }

    [HttpGet("words")]
    [SwaggerOperation(Summary = "A page of mined words, most recently changed first; guessed limits it to words with a note matched by guess")]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 0, [FromQuery] bool guessed = false,
                                          CancellationToken ct = default)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await mined.ListAsync(userId, search, page, ct, guessed));
    }

    [HttpGet("words/{minedWordId:long}")]
    [SwaggerOperation(Summary = "A mined word with its Anki notes and sentences")]
    public async Task<IActionResult> Get(long minedWordId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await mined.GetAsync(userId, minedWordId, ct) is { } word ? Ok(word) : NotFound();
    }

    [HttpGet("media")]
    [SwaggerOperation(Summary = "The caller's mined sentences whose cut media in Anki is behind their line's fix, with the names to store new cuts under")]
    public async Task<IActionResult> MediaBehind(CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return Ok(await mined.MediaBehindAsync(userId, ct));
    }

    [HttpPost("sentences/{minedSentenceId:long}/media")]
    [SwaggerOperation(Summary = "Records the names re-cut media was stored under in Anki")]
    public async Task<IActionResult> MediaStored(long minedSentenceId, [FromBody] MediaStoredRequest request, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        try
        {
            return await mined.MediaStoredAsync(userId, minedSentenceId, request, ct) ? NoContent() : NotFound();
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { message = e.Message });
        }
    }

    [HttpDelete("sentences/{minedSentenceId:long}")]
    [SwaggerOperation(Summary = "Removes a sentence from its word's list; the next Sync takes it off the card. Refused for the only sentence on the card.")]
    public async Task<IActionResult> RemoveSentence(long minedSentenceId, CancellationToken ct)
    {
        if (userService.UserId is not { } userId) return Unauthorized();
        return await mined.RemoveSentenceAsync(userId, minedSentenceId, ct) switch
        {
            null => NotFound(),
            false => Conflict(new { message = "It's the only sentence on the word's Anki card, or the sentence its Anki note was made with, so it can't be removed. Flag it to keep it off the card." }),
            true => NoContent(),
        };
    }
}
