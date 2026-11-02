using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <param name="Reason">japanese, english, media or parsing.</param>
public record SaveSentenceFlagRequest(long SentenceId, string Reason, string? Note);

/// <param name="Reason">Null when the flag came from a card and still needs one.</param>
/// <param name="HasPage">Whether the sentence has its own page: private lines don't.</param>
public record SentenceFlagItemDto(long SentenceFlagId, long SentenceId, string? Reason, string? Note, DateTime CreatedAt, string? Text, string? Source, bool HasPage);

/// <param name="Reason">Null when the flag came from a card without one.</param>
public record AdminSentenceFlagNoteDto(string? Reason, string Note, DateTime CreatedAt);

/// <param name="Title">The media the line is from: the parent deck, else the deck, else the line's own source.</param>
/// <param name="Episode">The child deck, when the line is in one.</param>
/// <param name="Reasons">How many open flags give each reason; "" counts flags with none yet.</param>
public record AdminSentenceFlagItemDto(long SentenceId, string Text, string? Title, string? Episode, int? DeckId, bool HasPage, bool RemovedFromDeck,
                                       int FlagCount, Dictionary<string, int> Reasons, List<AdminSentenceFlagNoteDto> Notes, DateTime FirstFlaggedAt);

public record AdminSentenceFlagPage(List<AdminSentenceFlagItemDto> Items, int TotalCount);

/// <summary>The user's flagged sentences: what they hide from Mine and card rotation, and the to-do list of them.</summary>
public class SentenceFlagService(UserDbContext userContext, IDbContextFactory<JitenDbContext> contextFactory)
{
    public const int MaxNoteLength = 1000;

    public async Task<HashSet<long>> OpenSentenceIdsAsync(string userId, CancellationToken ct) =>
        (await userContext.SentenceFlags.AsNoTracking().Where(f => f.UserId == userId && f.ResolvedAt == null)
                          .Select(f => f.ExampleSentenceId).ToListAsync(ct)).ToHashSet();

    /// <summary>The open flags, oldest first. A line that has since gone keeps its flag, with no text.</summary>
    public async Task<List<SentenceFlagItemDto>> ListAsync(string userId, CancellationToken ct)
    {
        var flags = await userContext.SentenceFlags.AsNoTracking().Where(f => f.UserId == userId && f.ResolvedAt == null)
                                     .OrderBy(f => f.CreatedAt).ThenBy(f => f.SentenceFlagId).ToListAsync(ct);
        var ids = flags.Select(f => f.ExampleSentenceId).ToList();

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var lines = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                 .Where(s => ids.Contains(s.SentenceId) && (s.OwnerId == null || s.OwnerId == userId))
                                 .Select(s => new { s.SentenceId, s.Text, s.SourceTitle, Deck = s.Deck == null ? null : s.Deck.OriginalTitle, s.RemovedFromDeck, s.DeckId })
                                 .ToDictionaryAsync(s => s.SentenceId, ct);

        return flags.Select(f =>
        {
            var line = lines.GetValueOrDefault(f.ExampleSentenceId);
            return new SentenceFlagItemDto(f.SentenceFlagId, f.ExampleSentenceId, f.Reason, f.Note, f.CreatedAt, line?.Text, line?.SourceTitle ?? line?.Deck,
                                       line is { RemovedFromDeck: false, DeckId: not null });
        }).ToList();
    }

    /// <summary>Flags a sentence, or sets the reason and note of its open flag. False when the user can't see the sentence.</summary>
    public async Task<bool> SaveAsync(string userId, SaveSentenceFlagRequest request, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var visible = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                   .AnyAsync(s => s.SentenceId == request.SentenceId && (s.OwnerId == null || s.OwnerId == userId), ct);
        if (!visible) return false;

        var flag = await userContext.SentenceFlags.FirstOrDefaultAsync(f => f.UserId == userId && f.ExampleSentenceId == request.SentenceId && f.ResolvedAt == null, ct);
        if (flag == null) userContext.SentenceFlags.Add(flag = new SentenceFlag { UserId = userId, ExampleSentenceId = request.SentenceId });

        flag.Reason = request.Reason;
        flag.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await userContext.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Closes an open flag without a fix; the sentence comes back. False when the user has no such flag.</summary>
    public async Task<bool> DismissAsync(string userId, long sentenceFlagId, CancellationToken ct) =>
        await userContext.SentenceFlags.Where(f => f.SentenceFlagId == sentenceFlagId && f.UserId == userId && f.ResolvedAt == null)
                         .ExecuteUpdateAsync(u => u.SetProperty(f => f.ResolvedAt, DateTime.UtcNow)
                                                     .SetProperty(f => f.Resolution, SentenceFlagResolution.Dismissed), ct) > 0;

    /// <summary>
    /// Flagged public lines for admins, one item per line: most flagged first, then longest waiting. Private lines are left
    /// out; only their owner sees them.
    /// </summary>
    public async Task<AdminSentenceFlagPage> AdminListAsync(string? reason, int offset, int limit, CancellationToken ct)
    {
        var flags = await userContext.SentenceFlags.AsNoTracking().Where(f => f.ResolvedAt == null)
                                     .Select(f => new { f.ExampleSentenceId, f.Reason, f.Note, f.CreatedAt }).ToListAsync(ct);
        var ids = flags.Select(f => f.ExampleSentenceId).Distinct().ToList();

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var lines = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                 .Where(s => ids.Contains(s.SentenceId) && s.OwnerId == null)
                                 .Select(s => new
                                 {
                                     s.SentenceId, s.Text, s.DeckId, s.RemovedFromDeck, s.SourceTitle,
                                     Deck = s.Deck == null ? null : s.Deck.OriginalTitle,
                                     Parent = s.Deck == null || s.Deck.ParentDeck == null ? null : s.Deck.ParentDeck.OriginalTitle,
                                 })
                                 .ToDictionaryAsync(s => s.SentenceId, ct);

        var items = flags.GroupBy(f => f.ExampleSentenceId)
                         .Where(g => lines.ContainsKey(g.Key) && (reason == null || g.Any(f => f.Reason == reason)))
                         .Select(g =>
                         {
                             var line = lines[g.Key];
                             var reasons = g.GroupBy(f => f.Reason ?? "").ToDictionary(r => r.Key, r => r.Count());
                             var notes = g.Where(f => f.Note != null).OrderBy(f => f.CreatedAt)
                                          .Select(f => new AdminSentenceFlagNoteDto(f.Reason, f.Note!, f.CreatedAt)).ToList();
                             return new AdminSentenceFlagItemDto(g.Key, line.Text, line.Parent ?? line.Deck ?? line.SourceTitle,
                                                                 line.Parent == null ? null : line.Deck, line.DeckId,
                                                                 line is { RemovedFromDeck: false, DeckId: not null }, line.RemovedFromDeck,
                                                                 g.Count(), reasons, notes, g.Min(f => f.CreatedAt));
                         })
                         .OrderByDescending(i => i.FlagCount).ThenBy(i => i.FirstFlaggedAt).ThenBy(i => i.SentenceId)
                         .ToList();

        return new AdminSentenceFlagPage(items.Skip(offset).Take(limit).ToList(), items.Count);
    }

    /// <summary>Closes every open flag on a line as fixed, bringing it back for everyone who flagged it; returns how many.</summary>
    public Task<int> MarkFixedAsync(long sentenceId, CancellationToken ct) =>
        userContext.SentenceFlags.Where(f => f.ExampleSentenceId == sentenceId && f.ResolvedAt == null)
                   .ExecuteUpdateAsync(u => u.SetProperty(f => f.ResolvedAt, DateTime.UtcNow)
                                             .SetProperty(f => f.Resolution, SentenceFlagResolution.Fixed), ct);
}
