using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>Lines Sync should add to one word's card, each marked at the word, in the order to add them.</summary>
public record AutoPlanWordDto(long MinedWordId, long AnkiNoteId, string ModelName, string Expression, List<ExampleSentenceDto> Sentences);

/// <param name="Capped">More lines qualified than the limit per Sync allows; the next Sync adds more.</param>
/// <param name="StartedAt">The server's time when the plan was made, for listing the words this Sync added to.</param>
public record AutoPlanDto(List<AutoPlanWordDto> Words, int Lines, int MaxPerSync, bool Capped, DateTime StartedAt);

/// <summary>What the next Sync would add for one note type, before the limit per Sync.</summary>
public record AutoPreviewDto(string ModelName, int Words, int Lines, int MaxPerSync);

/// <summary>
/// Automatic sentences: picks the lines Sync adds by itself to mined words' cards, from imported anime, TV and film.
/// A word gets lines while its card has fewer than its note type's <see cref="AnkiAutoSentencesDto.PerCard"/>, at most
/// <see cref="AnkiAutoSentencesDto.PerSync"/> in one Sync. The browser cuts and stores each line's media and adds it
/// through <see cref="MinedSentenceService.AddToNoteAsync"/>, marked as automatic.
/// </summary>
public class MinedAutoSentenceService(
    UserDbContext userContext,
    JitenDbContext context,
    ICurrentUserService currentUser,
    IExampleSentenceQueryService exampleSentences,
    SentenceFlagService sentenceFlags,
    AnkiSyncSetupService setups)
{
    /// <summary>Candidate lines read per word; the easiest first, so a very common word still finds short, easy ones.</summary>
    public const int CandidatesPerWord = 400;

    private const int WordsPerQuery = 50;

    /// <summary>A line that could be added: where it's from and what ranks it.</summary>
    public sealed record Candidate(long SentenceId, int DeckId, int TitleId, string Text, float Difficulty, byte Position, byte Length);

    private sealed record WordWork(MinedWord Word, MinedNote Note, AnkiAutoSentencesDto Settings, int Take);

    private sealed record DeckInfo(int TitleId, bool Speech, bool HasClip, bool HasTrack);

    public async Task<AutoPlanDto> PlanAsync(string userId, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var max = await setups.AutoMaxPerSyncAsync(userId, ct);
        var picks = await PickAsync(userId, null, max, ct);

        var ids = picks.SelectMany(p => p.Lines.Select(l => l.SentenceId)).Distinct().ToList();
        var dtos = (await exampleSentences.BuildDtosAsync(ids, 0, 0)).ToDictionary(d => d.SentenceId);
        var words = picks.Select(p => new AutoPlanWordDto(
                             p.Work.Word.MinedWordId, p.Work.Note.AnkiNoteId, p.Work.Note.ModelName, p.Work.Word.Expression,
                             p.Lines.Where(l => dtos.ContainsKey(l.SentenceId)).Select(l => dtos[l.SentenceId].ForWord(l.Position, l.Length)).ToList()))
                         .Where(w => w.Sentences.Count > 0)
                         .ToList();
        return new AutoPlanDto(words, words.Sum(w => w.Sentences.Count), max, picks.Capped, startedAt);
    }

    public async Task<AutoPreviewDto> PreviewAsync(string userId, string modelName, CancellationToken ct)
    {
        var max = await setups.AutoMaxPerSyncAsync(userId, ct);
        var picks = await PickAsync(userId, modelName, int.MaxValue, ct);
        return new AutoPreviewDto(modelName, picks.Count(p => p.Lines.Count > 0), picks.Sum(p => p.Lines.Count), max);
    }

    private sealed class Picks : List<(WordWork Work, List<Candidate> Lines)>
    {
        public bool Capped { get; set; }
    }

    /// <param name="onlyModel">Only words whose card is of this note type, which may be off (for the preview while editing).</param>
    private async Task<Picks> PickAsync(string userId, string? onlyModel, int budget, CancellationToken ct)
    {
        var result = new Picks();
        var work = await WordsToFillAsync(userId, onlyModel, ct);
        if (work.Count == 0) return result;

        var decks = await DecksAsync(ct);
        var flagged = await sentenceFlags.OpenSentenceIdsAsync(userId, ct);
        var wordIds = work.Select(w => w.Word.MinedWordId).ToList();
        var rejected = (await userContext.MinedSentenceRejections.AsNoTracking()
                                         .Where(r => r.UserId == userId && wordIds.Contains(r.MinedWordId))
                                         .Select(r => new { r.MinedWordId, r.ExampleSentenceId }).ToListAsync(ct))
                       .GroupBy(r => r.MinedWordId).ToDictionary(g => g.Key, g => g.Select(r => r.ExampleSentenceId).ToHashSet());

        // What each word's list already holds: its lines' decks and texts, so repeats and its titles count.
        var listed = work.SelectMany(w => w.Word.Sentences).Where(s => s.ExampleSentenceId != null).Select(s => s.ExampleSentenceId!.Value).Distinct().ToList();
        var listedLines = (await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters().Where(s => listed.Contains(s.SentenceId))
                                        .Select(s => new { s.SentenceId, s.DeckId, s.Text }).ToListAsync(ct))
                          .ToDictionary(s => s.SentenceId);

        foreach (var chunk in work.Chunk(WordsPerQuery))
        {
            var candidates = await CandidatesAsync(chunk, decks, ct);
            var targets = chunk.Select(w => new SentenceToken(w.Word.WordId!.Value, w.Word.ReadingIndex ?? 0, 0, 0, true, false)).ToArray();
            var known = await StatusesAsync(candidates.Values.SelectMany(c => c.Select(x => x.Tokens)).Append(targets), ct);

            foreach (var w in chunk)
            {
                var settings = w.Settings;
                var key = ExampleSentenceTokens.WordKey(w.Word.WordId!.Value, w.Word.ReadingIndex ?? 0);
                // Words you've settled (mastered, redundant, blacklisted) are never targets.
                if (known.GetValueOrDefault(key) == MiningStatus.Settled) continue;
                var have = w.Word.Sentences.Where(s => s.ExampleSentenceId != null).Select(s => s.ExampleSentenceId!.Value).ToHashSet();
                var texts = w.Word.Sentences.Select(s => s.ExampleSentenceId is { } id && listedLines.TryGetValue(id, out var l)
                                                             ? Normalize(l.Text)
                                                             : Normalize(MinedLineStore.PlainText(s.Text ?? "")))
                             .ToHashSet();
                var titles = new HashSet<int>();
                var episodes = new HashSet<int>();
                foreach (var id in have)
                    if (listedLines.TryGetValue(id, out var l) && l.DeckId is { } deck)
                    {
                        episodes.Add(deck);
                        titles.Add(decks.TryGetValue(deck, out var info) ? info.TitleId : deck);
                    }

                var excluded = (settings.ExcludedTitles ?? []).Select(t => t.DeckId).ToHashSet();
                var skip = rejected.GetValueOrDefault(w.Word.MinedWordId) ?? [];
                var eligible = new List<Candidate>();
                foreach (var (row, tokens) in candidates.GetValueOrDefault(key) ?? [])
                {
                    var deck = decks[row.DeckId];
                    if (have.Contains(row.SentenceId) || skip.Contains(row.SentenceId) || flagged.Contains(row.SentenceId)) continue;
                    if (excluded.Contains(deck.TitleId) || excluded.Contains(row.DeckId)) continue;
                    if (settings.NeedsClip && !deck.HasClip) continue;
                    if (settings.NeedsEnglish && !deck.HasTrack && !row.HasTranslation) continue;
                    var length = row.Text.Length;
                    if (length < settings.LengthMin || length > settings.LengthMax) continue;
                    if (texts.Contains(Normalize(row.Text))) continue;
                    var target = ExampleSentenceTokens.FindForm(tokens, w.Word.WordId!.Value, w.Word.ReadingIndex ?? 0);
                    if (target is not { } t || !OthersKnown(tokens, key, known, settings.OthersMature)) continue;
                    eligible.Add(new Candidate(row.SentenceId, row.DeckId, deck.TitleId, row.Text, row.Difficulty, t.Position, t.Length));
                }

                var lines = Rank(eligible, titles, episodes, w.Take);
                if (lines.Count > budget)
                {
                    // Capped only when a line was actually left for the next Sync.
                    result.Capped = true;
                    if (budget > 0) result.Add((w, lines.Take(budget).ToList()));
                    return result;
                }

                budget -= lines.Count;
                result.Add((w, lines));
            }
        }

        return result;
    }

    /// <summary>
    /// Picks up to <paramref name="take"/> lines: from titles the word has no line from yet, then episodes it has none
    /// from, then the easiest, then the shortest. At most one per episode, and never the same text twice.
    /// </summary>
    public static List<Candidate> Rank(IReadOnlyList<Candidate> eligible, IReadOnlySet<int> titles, IReadOnlySet<int> episodes, int take)
    {
        var usedTitles = new HashSet<int>(titles);
        var usedEpisodes = new HashSet<int>(episodes);
        var pickedEpisodes = new HashSet<int>();
        var pickedTexts = new HashSet<string>();
        var picked = new List<Candidate>();
        var pool = eligible.ToList();
        while (picked.Count < take)
        {
            var next = pool.Where(c => !pickedEpisodes.Contains(c.DeckId) && !pickedTexts.Contains(Normalize(c.Text)))
                           .OrderBy(c => usedTitles.Contains(c.TitleId) ? 1 : 0)
                           .ThenBy(c => usedEpisodes.Contains(c.DeckId) ? 1 : 0)
                           .ThenBy(c => c.Difficulty)
                           .ThenBy(c => c.Text.Length)
                           .ThenBy(c => c.SentenceId)
                           .FirstOrDefault();
            if (next == null) break;
            picked.Add(next);
            pool.Remove(next);
            usedTitles.Add(next.TitleId);
            usedEpisodes.Add(next.DeckId);
            pickedEpisodes.Add(next.DeckId);
            pickedTexts.Add(Normalize(next.Text));
        }

        return picked;
    }

    /// <summary>Every content word but the target is known well enough; names and function words don't count.</summary>
    public static bool OthersKnown(IEnumerable<SentenceToken> tokens, int targetKey, IReadOnlyDictionary<int, MiningStatus> statuses, bool othersMature) =>
        tokens.Where(t => !t.IsFunctionWord && !SentenceComprehension.IsNameEntry(t.WordId) && t.WordKey != targetKey)
              .All(t => statuses.GetValueOrDefault(t.WordKey, MiningStatus.New) is var s
                        && (s is MiningStatus.Settled or MiningStatus.Mature || (s == MiningStatus.Young && !othersMature)));

    private static string Normalize(string text) => new(text.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());

    /// <summary>Words with a card of a note type with automatic sentences on, the fewest sentences first, each with how many it can take.</summary>
    private async Task<List<WordWork>> WordsToFillAsync(string userId, string? onlyModel, CancellationToken ct)
    {
        var all = await setups.SetupsAsync(userId, ct);
        var byModel = all.ToDictionary(s => s.ModelName);
        // Lines reach a card only through its sentence list.
        var auto = all.Where(s => s.SentenceList != AnkiSentenceListFormat.None && AnkiSyncSetupService.Roles(s).ContainsValue("sentenceList"))
                      .Select(s => (s.ModelName, Settings: AnkiSyncSetupService.AutoSentences(s)))
                      .Where(s => s.Settings != null && (onlyModel == null ? s.Settings.Enabled : s.ModelName == onlyModel))
                      .ToDictionary(s => s.ModelName, s => s.Settings!);
        if (auto.Count == 0) return [];

        var models = auto.Keys.ToList();
        var words = await userContext.MinedWords.AsNoTracking().AsSplitQuery()
                                     .Include(w => w.Notes).Include(w => w.Sentences)
                                     .Where(w => w.UserId == userId && w.WordId != null && w.Notes.Any(n => models.Contains(n.ModelName)))
                                     .ToListAsync(ct);
        var work = new List<WordWork>();
        foreach (var word in words)
        {
            if (word.Notes.Any(n => n.FoldingInto != null)) continue; // a fold is under way
            var note = MinedSyncService.KeptNote(word, byModel);
            if (note == null || !auto.TryGetValue(note.ModelName, out var settings)) continue;
            var onCard = word.Sentences.Count(s => !s.JitenOnly);
            var take = Math.Min(settings.PerCard - onCard, settings.PerSync);
            if (take > 0) work.Add(new WordWork(word, note, settings, take));
        }

        return work.OrderBy(w => w.Word.Sentences.Count(s => !s.JitenOnly)).ThenBy(w => w.Word.MinedWordId).ToList();
    }

    /// <summary>Episodes and films of imported anime, TV and film, with their title and whether they have a clip and English subtitles.</summary>
    private async Task<Dictionary<int, DeckInfo>> DecksAsync(CancellationToken ct)
    {
        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => d.MediaType == MediaType.Anime || d.MediaType == MediaType.Drama || d.MediaType == MediaType.Movie
                                             || (d.ParentDeck != null && (d.ParentDeck.MediaType == MediaType.Anime || d.ParentDeck.MediaType == MediaType.Drama
                                                                          || d.ParentDeck.MediaType == MediaType.Movie)))
                                 .Select(d => new { d.DeckId, d.ParentDeckId, d.MediaType, ParentType = d.ParentDeck != null ? d.ParentDeck.MediaType : (MediaType?)null })
                                 .ToListAsync(ct);
        // A linked video that's gone from the library (moved or renamed) can't be cut, so its lines count as having no clip.
        var clips = (await context.DeckVideoSources.AsNoTracking().Select(v => new { v.DeckId, v.VideoPath }).ToListAsync(ct))
                    .Where(v => File.Exists(v.VideoPath)).Select(v => v.DeckId).ToHashSet();
        var tracks = (await context.DeckEnglishTracks.AsNoTracking().Select(t => t.DeckId).ToListAsync(ct)).ToHashSet();
        return decks.ToDictionary(d => d.DeckId, d => new DeckInfo(
                                      d.ParentDeckId ?? d.DeckId,
                                      MediaTypes.IsSubtitleSpeech(d.ParentType ?? d.MediaType),
                                      clips.Contains(d.DeckId),
                                      tracks.Contains(d.DeckId)));
    }

    private sealed record Row(long SentenceId, int DeckId, string Text, float Difficulty, byte[] Tokens, bool HasTranslation);

    /// <summary>Each word's lines in imported anime, TV and film (not private lines), the easiest first.</summary>
    private async Task<Dictionary<int, List<(Row Row, SentenceToken[] Tokens)>>> CandidatesAsync(IEnumerable<WordWork> words,
        Dictionary<int, DeckInfo> decks, CancellationToken ct)
    {
        var speech = decks.Where(d => d.Value.Speech).Select(d => d.Key).ToList();
        var result = new Dictionary<int, List<(Row, SentenceToken[])>>();
        foreach (var w in words)
        {
            var key = ExampleSentenceTokens.WordKey(w.Word.WordId!.Value, w.Word.ReadingIndex ?? 0);
            if (result.ContainsKey(key)) continue;
            var rows = await context.ExampleSentences.AsNoTracking()
                                    .Where(s => s.WordKeys.Contains(key) && s.DeckId != null && s.OwnerId == null && speech.Contains(s.DeckId!.Value))
                                    .OrderBy(s => s.Difficulty).ThenBy(s => s.SentenceId)
                                    .Take(CandidatesPerWord)
                                    .Select(s => new Row(s.SentenceId, s.DeckId!.Value, s.Text, s.Difficulty, s.Tokens,
                                                         s.CentralSentenceId != null &&
                                                         context.ExampleSentenceTranslations.Any(t => t.CentralSentenceId == s.CentralSentenceId)))
                                    .ToListAsync(ct);
            result[key] = rows.Where(r => !ExampleSentenceTokens.IsPartial(r.Tokens))
                              .Select(r => (r, ExampleSentenceTokens.Decode(r.Tokens)))
                              .ToList();
        }

        return result;
    }

    private async Task<Dictionary<int, MiningStatus>> StatusesAsync(IEnumerable<SentenceToken[]> sentences, CancellationToken ct)
    {
        var forms = sentences.SelectMany(t => t)
                             .Where(t => !t.IsFunctionWord && !SentenceComprehension.IsNameEntry(t.WordId))
                             .Select(t => (t.WordId, t.ReadingIndex)).Distinct().ToList();
        if (forms.Count == 0) return [];
        ct.ThrowIfCancellationRequested();
        var states = await currentUser.GetKnownWordsState(forms);
        return states.ToDictionary(kv => ExampleSentenceTokens.WordKey(kv.Key.WordId, kv.Key.ReadingIndex),
                                   kv => SentenceComprehension.MiningStatusOf(kv.Value));
    }
}
