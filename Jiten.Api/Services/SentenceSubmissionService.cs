using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>The caller's latest submission of a line, for the fix screen.</summary>
/// <param name="Status">pending, approved or rejected.</param>
/// <param name="ReviewNote">The admin's reason, for a rejection.</param>
public record SubmissionStateDto(string Status, DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewNote);

/// <summary>A submission in the admin queue: the line as everyone sees it now, and what the user proposes.</summary>
/// <param name="Now">The shared line as it is: text, English, clip and frame.</param>
/// <param name="Proposed">Only the parts the submission changes; the rest are null.</param>
/// <param name="Changed">The line got another fix after this was submitted.</param>
/// <param name="SameText">How many same-text lines the submission also gives the new text.</param>
public record AdminSubmissionDto(long SubmissionId, long SentenceId, string? Title, string? Episode, int? DeckId, bool HasPage, bool HasVideo,
                                 bool RemovedFromDeck, string Submitter, DateTime CreatedAt, FixPartsDto Now, FixPartsDto Proposed, bool Changed,
                                 int SameText);

public record AdminSubmissionPage(List<AdminSubmissionDto> Items, int TotalCount);

/// <summary>
/// Fixes users submit for review (sentence flags step 4): submitting and withdrawing, and the admin queue that approves
/// them into the shared line or rejects them. An admin's own Submit skips this; see <see cref="SentenceFixService.SubmitAsync"/>.
/// </summary>
public class SentenceSubmissionService(
    JitenDbContext context,
    UserDbContext userContext,
    SentenceFixService fixes,
    NotificationService notifications,
    ILogger<SentenceSubmissionService> logger)
{
    /// <summary>How many submissions a user may have waiting at once.</summary>
    public const int MaxPending = 100;

    public const int MaxNoteLength = 1000;

    /// <summary>
    /// Submits the user's fix of a shared line for review, replacing their pending submission of it. Their fix stays
    /// theirs meanwhile. False when they can't see the line or have no fix of it.
    /// </summary>
    /// <exception cref="SentenceFixException">A private line, or too many waiting.</exception>
    public async Task<bool> SubmitAsync(string userId, long sentenceId, SubmitSentenceFixRequest request, CancellationToken ct)
    {
        var line = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                .FirstOrDefaultAsync(s => s.SentenceId == sentenceId && (s.OwnerId == null || s.OwnerId == userId), ct);
        var fix = await userContext.SentenceFixes.AsNoTracking().FirstOrDefaultAsync(f => f.UserId == userId && f.ExampleSentenceId == sentenceId, ct);
        if (line == null || fix == null || fix.IsEmpty) return false;
        if (line.OwnerId != null || line.DeckId == null) throw new SentenceFixException("Only lines from a title can be submitted.");

        try
        {
            await SnapshotAsync(userId, sentenceId, fix, request, ct);
        }
        catch (DbUpdateException)
        {
            // Another request (a double click, a second tab) added the pending row first: update that one instead
            context.ChangeTracker.Clear();
            await SnapshotAsync(userId, sentenceId, fix, request, ct);
        }

        logger.LogInformation("User {UserId} submitted a fix of sentence {SentenceId} for review", userId, sentenceId);
        return true;
    }

    /// <summary>Copies the fix into the user's pending submission of the line, adding one if there's none.</summary>
    private async Task SnapshotAsync(string userId, long sentenceId, SentenceFix fix, SubmitSentenceFixRequest request, CancellationToken ct)
    {
        var pending = await context.SentenceSubmissions.FirstOrDefaultAsync(s => s.UserId == userId && s.ExampleSentenceId == sentenceId &&
                                                                                 s.Status == SentenceSubmissionStatus.Pending, ct);
        if (pending == null)
        {
            var waiting = await Pending().CountAsync(s => s.UserId == userId, ct);
            if (waiting >= MaxPending) throw new SentenceFixException($"You have {MaxPending} fixes waiting for review. Submit more once some are reviewed.");
            context.SentenceSubmissions.Add(pending = new SentenceSubmission { UserId = userId, ExampleSentenceId = sentenceId });
        }

        (pending.Text, pending.English, pending.StartMs, pending.EndMs, pending.PictureMs) = (fix.Text, fix.English, fix.StartMs, fix.EndMs, fix.PictureMs);
        pending.SameTextIds = fix.Text == null ? [] : request.SameTextIds?.Distinct().ToArray() ?? [];
        pending.BaseCorrectionId = await LatestCorrectionAsync(sentenceId, ct);
        pending.CreatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
    }

    /// <summary>Pending submissions whose line still exists; one deleted outright can't be fixed, so it isn't waiting on anyone.</summary>
    private IQueryable<SentenceSubmission> Pending() =>
        context.SentenceSubmissions.Where(s => s.Status == SentenceSubmissionStatus.Pending &&
                                               context.ExampleSentences.IgnoreQueryFilters().Any(e => e.SentenceId == s.ExampleSentenceId));

    private Task<long?> LatestCorrectionAsync(long sentenceId, CancellationToken ct) =>
        context.SentenceCorrections.Where(c => c.ExampleSentenceId == sentenceId).MaxAsync(c => (long?)c.SentenceCorrectionId, ct);

    /// <summary>Withdraws the user's pending submission of a line. False when there's none.</summary>
    public async Task<bool> WithdrawAsync(string userId, long sentenceId, CancellationToken ct) =>
        await context.SentenceSubmissions.Where(s => s.UserId == userId && s.ExampleSentenceId == sentenceId && s.Status == SentenceSubmissionStatus.Pending)
                     .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SentenceSubmissionStatus.Withdrawn), ct) > 0;

    /// <summary>The user's pending submission of a line, else their latest reviewed one; null when there's neither.</summary>
    public Task<SubmissionStateDto?> StateAsync(string userId, long sentenceId, CancellationToken ct) =>
        context.SentenceSubmissions.AsNoTracking()
               .Where(s => s.UserId == userId && s.ExampleSentenceId == sentenceId && s.Status != SentenceSubmissionStatus.Withdrawn)
               .OrderBy(s => s.Status == SentenceSubmissionStatus.Pending ? 0 : 1).ThenByDescending(s => s.SentenceSubmissionId)
               .Select(s => new SubmissionStateDto(s.Status, s.CreatedAt, s.ReviewedAt, s.ReviewNote))
               .FirstOrDefaultAsync(ct);

    public Task<int> PendingCountAsync(CancellationToken ct) => Pending().CountAsync(ct);

    /// <summary>The pending submissions, oldest first.</summary>
    public async Task<AdminSubmissionPage> AdminListAsync(int offset, int limit, CancellationToken ct)
    {
        var query = Pending().AsNoTracking();
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.SentenceSubmissionId).Skip(offset).Take(limit).ToListAsync(ct);

        var lineIds = rows.Select(r => r.ExampleSentenceId).Distinct().ToList();
        var lines = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters().Include(s => s.Deck).ThenInclude(d => d!.ParentDeck)
                                 .Where(s => lineIds.Contains(s.SentenceId)).ToDictionaryAsync(s => s.SentenceId, ct);
        var latest = await context.SentenceCorrections.AsNoTracking().Where(c => lineIds.Contains(c.ExampleSentenceId))
                                  .GroupBy(c => c.ExampleSentenceId).Select(g => new { g.Key, Id = g.Max(c => c.SentenceCorrectionId) })
                                  .ToDictionaryAsync(g => g.Key, g => (long?)g.Id, ct);
        var deckIds = lines.Values.Where(l => l.DeckId != null).Select(l => l.DeckId!.Value).Distinct().ToList();
        var withVideo = (await context.DeckVideoSources.AsNoTracking().Where(v => deckIds.Contains(v.DeckId)).Select(v => v.DeckId).ToListAsync(ct)).ToHashSet();
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var names = await userContext.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "", ct);

        var items = new List<AdminSubmissionDto>();
        foreach (var row in rows)
        {
            // Deleted since the query above
            if (!lines.TryGetValue(row.ExampleSentenceId, out var line)) continue;
            var now = await fixes.SharedLineAsync(line, ct);
            var (title, episode) = SentenceFixService.Titles(line);
            items.Add(new AdminSubmissionDto(
                row.SentenceSubmissionId, row.ExampleSentenceId, title, episode, line.DeckId, line is { DeckId: not null, RemovedFromDeck: false },
                line.DeckId is { } d && withVideo.Contains(d), line.RemovedFromDeck, names.GetValueOrDefault(row.UserId, ""), row.CreatedAt, now,
                new FixPartsDto(row.Text, row.English, row.StartMs, row.EndMs, row.PictureMs),
                latest.GetValueOrDefault(row.ExampleSentenceId) != row.BaseCorrectionId, row.SameTextIds.Length));
        }

        return new AdminSubmissionPage(items, total);
    }

    /// <summary>
    /// Approves a pending submission: its parts become the shared line's, as an admin's Submit would make them, and the
    /// submitter is told. False when it isn't pending or its line is gone.
    /// </summary>
    /// <exception cref="SentenceFixException">The new text couldn't be parsed.</exception>
    public async Task<bool> ApproveAsync(long submissionId, string reviewerId, CancellationToken ct)
    {
        var submission = await context.SentenceSubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.SentenceSubmissionId == submissionId &&
                                                                                                  s.Status == SentenceSubmissionStatus.Pending, ct);
        if (submission == null) return false;
        var line = await context.ExampleSentences.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.SentenceId == submission.ExampleSentenceId && s.OwnerId == null, ct);
        if (line == null) return false;

        // Claimed first, so a second approval of the same submission (two admins, a retry) finds it no longer pending
        var claimed = await context.SentenceSubmissions.Where(s => s.SentenceSubmissionId == submissionId && s.Status == SentenceSubmissionStatus.Pending)
                                   .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SentenceSubmissionStatus.Approved)
                                                             .SetProperty(s => s.ReviewerId, reviewerId)
                                                             .SetProperty(s => s.ReviewedAt, DateTime.UtcNow), ct);
        if (claimed == 0) return false;
        try
        {
            await fixes.ApplyToSharedAsync(line, new FixPartsDto(submission.Text, submission.English, submission.StartMs, submission.EndMs, submission.PictureMs),
                                           submission.SameTextIds, submission.UserId, ct);
        }
        catch (SentenceFixException)
        {
            // Nothing was applied (the text didn't parse): it waits again
            await context.SentenceSubmissions.Where(s => s.SentenceSubmissionId == submissionId)
                         .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SentenceSubmissionStatus.Pending)
                                                   .SetProperty(s => s.ReviewerId, (string?)null)
                                                   .SetProperty(s => s.ReviewedAt, (DateTime?)null), ct);
            throw;
        }

        await notifications.Notify(submission.UserId, NotificationType.General, "Your fix was approved",
                                   $"「{line.Text}」 is now the line everyone sees.", $"/sentences/{line.SentenceId}");
        logger.LogInformation("Admin {ReviewerId} approved submission {SubmissionId} of sentence {SentenceId}", reviewerId, submissionId, line.SentenceId);
        return true;
    }

    /// <summary>Rejects a pending submission, with an optional reason for the submitter, who is told. False when it isn't pending.</summary>
    public async Task<bool> RejectAsync(long submissionId, string reviewerId, string? note, CancellationToken ct)
    {
        var submission = await context.SentenceSubmissions.FirstOrDefaultAsync(s => s.SentenceSubmissionId == submissionId &&
                                                                                    s.Status == SentenceSubmissionStatus.Pending, ct);
        if (submission == null) return false;
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        (submission.Status, submission.ReviewerId, submission.ReviewedAt, submission.ReviewNote) = (SentenceSubmissionStatus.Rejected, reviewerId, DateTime.UtcNow, note);
        await context.SaveChangesAsync(ct);

        var text = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters().Where(s => s.SentenceId == submission.ExampleSentenceId)
                                .Select(s => s.Text).FirstOrDefaultAsync(ct);
        await notifications.Notify(submission.UserId, NotificationType.General, "Your fix wasn't accepted",
                                   $"「{text}」{(note == null ? "" : $": {note}")} Your own fix is still yours.", $"/sentences/{submission.ExampleSentenceId}/fix");
        logger.LogInformation("Admin {ReviewerId} rejected submission {SubmissionId}", reviewerId, submissionId);
        return true;
    }
}
