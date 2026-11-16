using System.Text.Json;
using Jiten.Api.Services.Translation;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public record CheckContextLine(string Text, string? English);

/// <summary>Lines with the same text and English, checked once; the verdict is posted for all of <see cref="Ids"/>.</summary>
public record CheckBatchItem(List<long> Ids, string Title, string? Episode, string Text, string English, string Origin,
                             List<CheckContextLine> Before, List<CheckContextLine> After);

/// <param name="Exhausted">Fewer lines than asked for were left to do in the decks scanned.</param>
/// <param name="LeaseId">Sent back with the verdicts: a verdict for a line leased again since is refused.</param>
public record CheckBatch(Guid LeaseId, List<CheckBatchItem> Items, int Lines, bool Exhausted, DateTime LeasedUntil);

public record CheckResult(List<long> Ids, List<SentenceCheckFinding>? Findings);

public record CheckResultsRequest(Guid LeaseId, string Checker, List<CheckResult> Results);

/// <param name="Rejected">Lines not leased, or whose text or English changed since they were leased.</param>
public record CheckResultsSummary(int Passed, int Flagged, int Rejected);

public record CheckQueueItem(long Id, int DeckId, string Title, string? Episode, int Position, string Text, string? English, string? Origin,
                             List<SentenceCheckFinding> Findings, string Status, string? Checker, DateTime? CheckedAt, string? ResolutionNote);

public record CheckProgress(int DeckId, string Title, int Lines, int Passed, int Flagged, int Accepted, int Dismissed, int Leased);

public record AcceptCheckRequest(string? English, string? Text, List<long>? SameTextIds);

public record DismissCheckRequest(string? Note);

public class SentenceCheckException(string message) : Exception(message);

/// <summary>
/// Bulk checks of deck lines' text and English by Claude Code sessions (GDR-060): lease a batch, post verdicts, and the
/// admin review queue that accepts or dismisses what was flagged. A verdict changes nothing until an admin accepts it.
/// </summary>
public class SentenceCheckService(
    JitenDbContext context,
    UserDbContext userContext,
    SentenceTranslationService translations,
    SentenceFixService fixes,
    IMemoryCache cache,
    ILogger<SentenceCheckService> logger)
{
    public const int MaxBatch = 100;
    public const int ContextLines = 3;
    public static readonly TimeSpan LeaseTime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a deck's English is reused while picking lines. Leasing a line with English that changed in this window is
    /// harmless: the verdict is refused (the English is compared afresh) and the line comes back.
    /// </summary>
    private static readonly TimeSpan EnglishReuse = TimeSpan.FromMinutes(10);

    public const string ReviewedProvider = "reviewed";

    /// <summary>One lease at a time, so parallel checker sessions never get the same line.</summary>
    private static readonly SemaphoreSlim LeaseLock = new(1, 1);

    /// <summary>
    /// Leases up to <paramref name="size"/> lines to check, from the given titles (parent decks) or every deck: lines with
    /// English that have no check, an expired lease, or a check of a different text or English.
    /// </summary>
    public async Task<CheckBatch> NextAsync(IReadOnlyCollection<int>? titleIds, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 1, MaxBatch);
        var decks = await DecksAsync(titleIds, ct);

        await LeaseLock.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var leasedUntil = now + LeaseTime;
            var leaseId = Guid.NewGuid();
            var picked = new List<(ExampleSentenceLine Line, SentenceTranslationDto English, DeckInfo Deck, List<ExampleSentenceLine> Neighbours,
                                   Dictionary<long, SentenceTranslationDto> DeckEnglish)>();

            foreach (var deck in decks)
            {
                if (picked.Count >= size) break;
                var lines = await context.ExampleSentences.AsNoTracking().Where(s => s.DeckId == deck.DeckId).OrderBy(s => s.Position)
                                         .Select(s => new ExampleSentenceLine(s.SentenceId, s.Text, s.Position)).ToListAsync(ct);
                var checks = await context.SentenceChecks.AsNoTracking().Where(c => c.DeckId == deck.DeckId).ToDictionaryAsync(c => c.ExampleSentenceId, ct);
                // Aligning a deck's subtitles is the costly part; later runs walk past mostly checked decks, so reuse it briefly
                var english = (await cache.GetOrCreateAsync($"check-english:{deck.DeckId}", entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = EnglishReuse;
                    return translations.EnglishForDeckAsync(deck.DeckId, ct);
                }))!;
                foreach (var line in lines)
                {
                    if (picked.Count >= size) break;
                    if (english.GetValueOrDefault(line.Id) is not { } en) continue;
                    if (checks.GetValueOrDefault(line.Id) is { } check && !IsToDo(check, line.Text, en.Text, now)) continue;
                    picked.Add((line, en, deck, lines, english));
                }
            }

            var ids = picked.Select(p => p.Line.Id).ToList();
            var rows = await context.SentenceChecks.Where(c => ids.Contains(c.ExampleSentenceId)).ToDictionaryAsync(c => c.ExampleSentenceId, ct);
            foreach (var (line, en, deck, _, _) in picked)
            {
                if (!rows.TryGetValue(line.Id, out var row))
                    context.SentenceChecks.Add(row = new SentenceCheck { ExampleSentenceId = line.Id });
                row.DeckId = deck.DeckId;
                row.CheckedText = line.Text;
                row.CheckedEnglish = en.Text;
                row.EnglishOrigin = en.Origin;
                row.Status = SentenceCheckStatus.Leased;
                row.LeasedUntil = leasedUntil;
                row.LeaseId = leaseId;
                row.FindingsJson = "[]";
                (row.Checker, row.CheckedAt, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote) = (null, null, null, null, null);
            }

            await context.SaveChangesAsync(ct);

            var items = picked.GroupBy(p => (p.Line.Text, p.English.Text))
                              .Select(g =>
                              {
                                  var first = g.First();
                                  var at = first.Neighbours.FindIndex(l => l.Id == first.Line.Id);
                                  CheckContextLine Context(ExampleSentenceLine l) => new(l.Text, first.DeckEnglish.GetValueOrDefault(l.Id)?.Text);
                                  return new CheckBatchItem(g.Select(p => p.Line.Id).ToList(), first.Deck.Title, first.Deck.Episode, first.Line.Text,
                                                            first.English.Text, first.English.Origin,
                                                            first.Neighbours.Skip(Math.Max(0, at - ContextLines)).Take(Math.Min(at, ContextLines)).Select(Context).ToList(),
                                                            first.Neighbours.Skip(at + 1).Take(ContextLines).Select(Context).ToList());
                              })
                              .ToList();

            logger.LogInformation("Leased {Lines} lines ({Items} distinct) for checking", picked.Count, items.Count);
            return new CheckBatch(leaseId, items, picked.Count, picked.Count < size, leasedUntil);
        }
        finally
        {
            LeaseLock.Release();
        }
    }

    /// <summary>Stores verdicts for leased lines whose text and English are still what was leased; no findings means passed.</summary>
    public async Task<CheckResultsSummary> PostResultsAsync(CheckResultsRequest request, CancellationToken ct)
    {
        foreach (var finding in request.Results.SelectMany(r => r.Findings ?? []))
        {
            if (!SentenceCheckKind.All.Contains(finding.Kind)) throw new SentenceCheckException($"Unknown finding kind \"{finding.Kind}\".");
            if (string.IsNullOrWhiteSpace(finding.Note)) throw new SentenceCheckException("Every finding needs a note.");
        }

        var ids = request.Results.SelectMany(r => r.Ids).Distinct().ToList();
        var rows = await context.SentenceChecks.Where(c => ids.Contains(c.ExampleSentenceId)).ToDictionaryAsync(c => c.ExampleSentenceId, ct);
        var texts = await context.ExampleSentences.AsNoTracking().Where(s => ids.Contains(s.SentenceId))
                                 .ToDictionaryAsync(s => s.SentenceId, s => new { s.Text, s.DeckId }, ct);
        var english = new Dictionary<long, SentenceTranslationDto>();
        foreach (var deckId in texts.Values.Select(t => t.DeckId!.Value).Distinct())
            foreach (var (id, en) in await translations.EnglishForDeckAsync(deckId, ct))
                english[id] = en;

        int passed = 0, flagged = 0, rejected = 0;
        var now = DateTime.UtcNow;
        foreach (var result in request.Results)
        {
            var findings = (result.Findings ?? []).Select(Clean).ToList();
            foreach (var id in result.Ids.Distinct())
            {
                if (!rows.TryGetValue(id, out var row) || row.Status != SentenceCheckStatus.Leased || row.LeaseId != request.LeaseId ||
                    texts.GetValueOrDefault(id)?.Text != row.CheckedText || english.GetValueOrDefault(id)?.Text != row.CheckedEnglish)
                {
                    rejected++;
                    continue;
                }

                row.SetFindings(findings);
                row.Status = findings.Count == 0 ? SentenceCheckStatus.Passed : SentenceCheckStatus.Flagged;
                (row.LeasedUntil, row.LeaseId, row.Checker, row.CheckedAt) = (null, null, Truncate(request.Checker, 100), now);
                if (findings.Count == 0) passed++;
                else flagged++;
            }
        }

        await context.SaveChangesAsync(ct);
        return new CheckResultsSummary(passed, flagged, rejected);
    }

    /// <summary>The review queue: checks in a status, optionally of one kind of finding and one title, in deck order.</summary>
    public async Task<(List<CheckQueueItem> Items, int Total)> QueueAsync(string status, string? kind, int? titleId, int offset, int limit, CancellationToken ct)
    {
        var query = context.SentenceChecks.AsNoTracking().Where(c => c.Status == status);
        if (!string.IsNullOrEmpty(kind))
        {
            // Findings are stored compact (SentenceCheck.SetFindings), so the kind appears exactly like this
            var pattern = $"\"kind\":{JsonSerializer.Serialize(kind)}";
            query = query.Where(c => c.FindingsJson.Contains(pattern));
        }
        if (titleId != null)
        {
            var deckIds = await context.Decks.AsNoTracking().Where(d => d.DeckId == titleId || d.ParentDeckId == titleId).Select(d => d.DeckId).ToListAsync(ct);
            query = query.Where(c => deckIds.Contains(c.DeckId));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.Join(context.ExampleSentences.AsNoTracking().IgnoreQueryFilters(), c => c.ExampleSentenceId, s => s.SentenceId,
                                    (c, s) => new { Check = c, s.Position, Deck = s.Deck!.OriginalTitle, Parent = s.Deck.ParentDeck!.OriginalTitle })
                              .OrderBy(r => r.Check.DeckId).ThenBy(r => r.Position)
                              .Skip(offset).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);

        return (rows.Select(r => new CheckQueueItem(r.Check.ExampleSentenceId, r.Check.DeckId, r.Parent ?? r.Deck, r.Parent != null ? r.Deck : null,
                                                    r.Position, r.Check.CheckedText, r.Check.CheckedEnglish, r.Check.EnglishOrigin,
                                                    r.Check.GetFindings(), r.Check.Status, r.Check.Checker, r.Check.CheckedAt, r.Check.ResolutionNote))
                    .ToList(), total);
    }

    /// <summary>
    /// Applies a flagged check's suggestion, or the admin's edit of it. Japanese becomes a shared text fix (with its
    /// correction row, so reparse keeps it). English replaces the cached translation when it was AI English, so every line
    /// with that text gets it; otherwise it becomes the shared fix's English on this line.
    /// </summary>
    /// <returns>False when there is no flagged check for the line.</returns>
    /// <exception cref="SentenceFixException">The new text couldn't be parsed, or the title's reparse is saving.</exception>
    public async Task<bool> AcceptAsync(long sentenceId, string adminId, AcceptCheckRequest request, CancellationToken ct)
    {
        var row = await context.SentenceChecks.FirstOrDefaultAsync(c => c.ExampleSentenceId == sentenceId && c.Status == SentenceCheckStatus.Flagged, ct);
        if (row == null) return false;
        var line = await context.ExampleSentences.FirstOrDefaultAsync(s => s.SentenceId == sentenceId, ct);
        if (line == null) return false;

        var text = Blank(request.Text) is { } t && t != line.Text ? t : null;
        var english = Blank(request.English) is { } e && e != row.CheckedEnglish ? e : null;
        if (line.Text != row.CheckedText)
            throw new SentenceCheckException("The line's text changed since it was checked. Dismiss this and check it again.");
        if (english != null && line.DeckId is { } deckId &&
            (await translations.EnglishForDeckAsync(deckId, ct)).GetValueOrDefault(sentenceId)?.Text != row.CheckedEnglish)
            throw new SentenceCheckException("The line's English changed since it was checked. Dismiss this and check it again.");

        var replacedCache = false;
        if (english != null && text == null && row.EnglishOrigin == TranslationOrigin.Ai && line.CentralSentenceId is { } centralId)
        {
            await translations.ReplaceCachedAsync(centralId, english, ReviewedProvider, Truncate(row.Checker ?? "claude-code", 100)!, ct);
            await MarkRebuildAsync(context.ExampleSentences.IgnoreQueryFilters().Where(s => s.CentralSentenceId == centralId).Select(s => s.SentenceId), ct);
            replacedCache = true;
        }
        else if (text != null || english != null)
        {
            await fixes.ApplyToSharedAsync(line, new FixPartsDto(text, english, null, null, null), request.SameTextIds ?? [], adminId, ct);
        }

        var now = DateTime.UtcNow;
        Resolve(row, SentenceCheckStatus.Accepted, adminId, now, null);
        row.CheckedText = text ?? row.CheckedText;
        row.CheckedEnglish = english ?? row.CheckedEnglish;

        // Other flagged lines with that text showed the same AI English, which is now replaced for them too
        if (replacedCache)
        {
            var same = await context.SentenceChecks
                                    .Where(c => c.ExampleSentenceId != sentenceId && c.Status == SentenceCheckStatus.Flagged &&
                                                c.EnglishOrigin == TranslationOrigin.Ai && c.CheckedText == row.CheckedText)
                                    .ToListAsync(ct);
            // Only their English findings are answered; any other finding stays for review
            foreach (var other in same)
            {
                other.CheckedEnglish = english;
                var rest = other.GetFindings().Where(f => f.Kind is not (SentenceCheckKind.EnglishFit or SentenceCheckKind.EnglishQuality)).ToList();
                other.SetFindings(rest);
                if (rest.Count == 0) Resolve(other, SentenceCheckStatus.Accepted, adminId, now, $"Same text as line {sentenceId}");
            }
        }

        await context.SaveChangesAsync(ct);
        if (line.DeckId is { } changedDeck) cache.Remove($"check-english:{changedDeck}");
        logger.LogInformation("Check of sentence {SentenceId} accepted by {UserId} (text {Text}, English {English})", sentenceId, adminId,
                              text != null, english != null);
        return true;
    }

    /// <returns>False when there is no flagged check for the line.</returns>
    public async Task<bool> DismissAsync(long sentenceId, string adminId, string? note, CancellationToken ct)
    {
        var row = await context.SentenceChecks.FirstOrDefaultAsync(c => c.ExampleSentenceId == sentenceId && c.Status == SentenceCheckStatus.Flagged, ct);
        if (row == null) return false;
        Resolve(row, SentenceCheckStatus.Dismissed, adminId, DateTime.UtcNow, Truncate(Blank(note), 1000));
        await context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Counts per title. Checks whose line has changed since are still counted as they were.</summary>
    public async Task<List<CheckProgress>> ProgressAsync(IReadOnlyCollection<int>? titleIds, CancellationToken ct)
    {
        var decks = await DecksAsync(titleIds, ct);
        var deckIds = decks.Select(d => d.DeckId).ToList();
        var lines = await context.ExampleSentences.AsNoTracking().Where(s => deckIds.Contains(s.DeckId!.Value))
                                 .GroupBy(s => s.DeckId!.Value).Select(g => new { DeckId = g.Key, Count = g.Count() })
                                 .ToDictionaryAsync(g => g.DeckId, g => g.Count, ct);
        var statuses = await context.SentenceChecks.AsNoTracking().Where(c => deckIds.Contains(c.DeckId))
                                    .GroupBy(c => new { c.DeckId, c.Status }).Select(g => new { g.Key.DeckId, g.Key.Status, Count = g.Count() })
                                    .ToListAsync(ct);

        return decks.GroupBy(d => (d.TitleId, d.Title))
                    .Select(g =>
                    {
                        var ids = g.Select(d => d.DeckId).ToHashSet();
                        int Count(string status) => statuses.Where(s => ids.Contains(s.DeckId) && s.Status == status).Sum(s => s.Count);
                        return new CheckProgress(g.Key.TitleId, g.Key.Title, ids.Sum(id => lines.GetValueOrDefault(id)),
                                                 Count(SentenceCheckStatus.Passed), Count(SentenceCheckStatus.Flagged), Count(SentenceCheckStatus.Accepted),
                                                 Count(SentenceCheckStatus.Dismissed), Count(SentenceCheckStatus.Leased));
                    })
                    .OrderBy(p => p.Title)
                    .ToList();
    }

    private record ExampleSentenceLine(long Id, string Text, int Position);

    /// <param name="TitleId">The parent deck, or the deck itself when it has none.</param>
    private record DeckInfo(int DeckId, int TitleId, string Title, string? Episode);

    /// <summary>Decks that hold lines, in the given titles (and their episodes) or all, in title then deck order.</summary>
    private async Task<List<DeckInfo>> DecksAsync(IReadOnlyCollection<int>? titleIds, CancellationToken ct)
    {
        var query = context.Decks.AsNoTracking().Where(d => context.ExampleSentences.Any(s => s.DeckId == d.DeckId));
        if (titleIds is { Count: > 0 })
            query = query.Where(d => titleIds.Contains(d.DeckId) || (d.ParentDeckId != null && titleIds.Contains(d.ParentDeckId.Value)));
        var decks = await query.Select(d => new { d.DeckId, d.ParentDeckId, d.OriginalTitle, Parent = d.ParentDeck!.OriginalTitle }).ToListAsync(ct);
        return decks.Select(d => new DeckInfo(d.DeckId, d.ParentDeckId ?? d.DeckId, d.Parent ?? d.OriginalTitle, d.ParentDeckId != null ? d.OriginalTitle : null))
                    .OrderBy(d => d.TitleId).ThenBy(d => d.DeckId)
                    .ToList();
    }

    private static bool IsToDo(SentenceCheck check, string text, string english, DateTime now) =>
        check.Status == SentenceCheckStatus.Leased
            ? check.LeasedUntil <= now
            : check.CheckedText != text || check.CheckedEnglish != english;

    private async Task MarkRebuildAsync(IQueryable<long> sentenceIds, CancellationToken ct)
    {
        var ids = await sentenceIds.ToListAsync(ct);
        await userContext.MinedSentences.Where(s => s.ExampleSentenceId != null && ids.Contains(s.ExampleSentenceId.Value) && !s.NeedsRebuild)
                         .ExecuteUpdateAsync(u => u.SetProperty(s => s.NeedsRebuild, true), ct);
    }

    private static void Resolve(SentenceCheck row, string status, string adminId, DateTime now, string? note) =>
        (row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote) = (status, now, adminId, note);

    private static SentenceCheckFinding Clean(SentenceCheckFinding f) =>
        new(f.Kind, Truncate(f.Note.Trim(), 1000)!, Truncate(Blank(f.SuggestedEnglish), 2000), Truncate(Blank(f.SuggestedText), 1000));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Truncate(string? s, int max) => s == null || s.Length <= max ? s : s[..max];
}
