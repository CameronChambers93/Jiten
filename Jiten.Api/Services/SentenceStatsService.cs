using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

/// <summary>Per-user sentence stats, held briefly so the detail card, the stats page and paging reuse one computation.</summary>
public sealed class SentenceStatsCache : IDisposable
{
    /// <summary>Budget in listing entries (about 24 bytes each); a stats result counts as 500.</summary>
    private const long SizeLimit = 1_000_000;

    public MemoryCache Cache { get; } = new(new MemoryCacheOptions { SizeLimit = SizeLimit });

    /// <summary>
    /// Forgets every user's stats and i+1 lists, after a deck's sentences changed underneath them (episode import).
    /// Entries are keyed by user, so one deck's can't be found to drop alone; they're cheap to rebuild.
    /// </summary>
    public void Clear() => Cache.Compact(1.0);

    public void Dispose() => Cache.Dispose();
}

public interface ISentenceStatsService
{
    /// <summary>Null when the deck doesn't exist. <paramref name="summaryOnly"/> skips the learn-next order and projection.</summary>
    /// <param name="knownVersion">The client's coverage version; a new one means the user's known words changed.</param>
    Task<DeckSentenceStatsDto?> GetStatsAsync(int deckId, bool summaryOnly, string? knownVersion);

    Task<DeckIPlusOneSentencesResponse?> GetIPlusOneSentencesAsync(int deckId, int offset, string? knownVersion);

    /// <summary>Unknown words of the deck, keyed by sentence word key, ranked by how many sentences learning them unlocks. Null when the deck has no sentence profiles.</summary>
    Task<IReadOnlyDictionary<int, int>?> GetUnlockRanksAsync(int deckId);

    /// <summary>Exact i+0 and i+1 shares, in percent, of each deck's own text; decks without a profile are left out.</summary>
    Task<Dictionary<int, (float Readable, float IPlusOne)>> GetOwnTextReadabilityAsync(IReadOnlyCollection<int> deckIds);

    /// <summary>A title's example sentences with exactly one unknown content word, for the user, in sentence id order. Null when the deck doesn't exist.</summary>
    Task<IPlusOneListing?> GetIPlusOneHitsAsync(int deckId);

    /// <summary>A title's example sentences under a mining rule, one hit per target word, in sentence id order. Null when the deck doesn't exist.</summary>
    Task<IPlusOneListing?> GetMiningHitsAsync(int deckId, MiningRule rule);

    /// <summary>Display summaries keyed by sentence word key.</summary>
    Task<Dictionary<int, WordSummaryDto>> LoadWordSummariesAsync(IEnumerable<int> wordKeys);
}

/// <summary>An example sentence whose one unknown content word is <paramref name="WordKey"/>, at <paramref name="Position"/> for <paramref name="Length"/> characters.</summary>
public sealed record IPlusOneHit(long SentenceId, int DeckId, int WordKey, byte Position, byte Length, float Difficulty, int TextLength, MiningStatus Status = MiningStatus.New, byte NewCount = 0, byte LearningCount = 0);

/// <param name="Checked">Example sentences judged; a long title holds more than the cap.</param>
public sealed record IPlusOneListing(List<IPlusOneHit> Hits, int Checked);

public class SentenceStatsService(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUser,
    IExampleSentenceQueryService exampleSentences,
    SentenceStatsCache cache) : ISentenceStatsService
{
    public const int IPlusOnePageSize = 20;

    /// <summary>Example sentences judged for the i+1 list; a long series holds far more, and the first ones already give plenty to mine.</summary>
    private const int IPlusOneCandidateCap = 20_000;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private const int SentencesPerWordFirst = 3;

    /// <summary>Known words drift slowly and already-carded words are filtered out anyway, so a stale order is harmless.</summary>
    private static readonly TimeSpan UnlockOrderTtl = TimeSpan.FromMinutes(10);

    private sealed record Part(int DeckId, int Order, string Title);

    private sealed record IPlusOneEntry(long SentenceId, int WordKey, byte Position, byte Length, int WordSentences = 0);

    public async Task<DeckSentenceStatsDto?> GetStatsAsync(int deckId, bool summaryOnly, string? knownVersion)
    {
        var parts = await LoadParts(deckId);
        if (parts == null) return null;

        var partIds = parts.Select(p => p.DeckId).ToList();
        var builds = await context.DeckSentenceProfiles.AsNoTracking()
                                  .Where(p => partIds.Contains(p.DeckId) && p.Profile != null)
                                  .Select(p => new { p.DeckId, p.BuiltAt })
                                  .ToListAsync();

        // A rebuilt profile or a new known-word state (the client's coverage version) must never be answered from the cache.
        var builtAt = builds.Count > 0 ? builds.Max(b => b.BuiltAt).Ticks : 0;
        var key = $"stats:{currentUser.UserId}:{deckId}:{summaryOnly}:{builds.Count}:{builtAt}:{knownVersion}:{await KnownStamp()}";
        if (cache.Cache.TryGetValue(key, out DeckSentenceStatsDto? cached)) return cached;

        var profiledIds = builds.Select(b => b.DeckId).ToList();
        var blobs = await context.DeckSentenceProfiles.AsNoTracking()
                                 .Where(p => profiledIds.Contains(p.DeckId))
                                 .Select(p => new { p.DeckId, p.Profile })
                                 .ToDictionaryAsync(p => p.DeckId, p => p.Profile!);

        var profiled = parts.Where(p => blobs.ContainsKey(p.DeckId)).ToList();
        var result = new DeckSentenceStatsDto { TotalParts = parts.Count, ProfiledParts = profiled.Count, SegmentsArePart = parts.Count > 1 };
        if (profiled.Count > 0)
        {
            var profiles = profiled.Select(p => SentenceProfileCodec.Decode(blobs[p.DeckId])).ToList();
            var isKnown = await KnownLookup(profiles.SelectMany(p => p.Keys));
            var stats = SentenceStatsCalculator.Compute(profiles, isKnown,
                                                        learnNextCount: summaryOnly ? 0 : 100, maxSteps: summaryOnly ? 0 : 2000);
            await Fill(result, stats, profiled, summaryOnly);
        }

        if (result.HasData)
            cache.Cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = 500 });
        return result;
    }

    public async Task<IReadOnlyDictionary<int, int>?> GetUnlockRanksAsync(int deckId)
    {
        var parts = await LoadParts(deckId);
        if (parts == null) return null;

        var partIds = parts.Select(p => p.DeckId).ToList();
        var builds = await context.DeckSentenceProfiles.AsNoTracking()
                                  .Where(p => partIds.Contains(p.DeckId) && p.Profile != null)
                                  .Select(p => new { p.DeckId, p.BuiltAt })
                                  .ToListAsync();
        if (builds.Count == 0) return null;

        var key = $"unlock:{currentUser.UserId}:{deckId}:{builds.Count}:{builds.Max(b => b.BuiltAt).Ticks}:{await KnownStamp()}";
        if (cache.Cache.TryGetValue(key, out IReadOnlyDictionary<int, int>? cached)) return cached;

        var profiledIds = builds.Select(b => b.DeckId).ToList();
        var blobs = await context.DeckSentenceProfiles.AsNoTracking()
                                 .Where(p => profiledIds.Contains(p.DeckId))
                                 .Select(p => new { p.DeckId, p.Profile })
                                 .ToDictionaryAsync(p => p.DeckId, p => p.Profile!);
        var profiles = parts.Where(p => blobs.ContainsKey(p.DeckId)).Select(p => SentenceProfileCodec.Decode(blobs[p.DeckId])).ToList();
        var isKnown = await KnownLookup(profiles.SelectMany(p => p.Keys));
        var stats = SentenceStatsCalculator.Compute(profiles, isKnown, learnNextCount: int.MaxValue, maxSteps: int.MaxValue);

        var ranks = new Dictionary<int, int>(stats.LearnNext.Count);
        for (int i = 0; i < stats.LearnNext.Count; i++)
            ranks.TryAdd(stats.LearnNext[i].WordKey, i);

        cache.Cache.Set(key, (IReadOnlyDictionary<int, int>)ranks,
                        new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = UnlockOrderTtl, Size = ranks.Count + 1 });
        return ranks;
    }

    public async Task<Dictionary<int, (float Readable, float IPlusOne)>> GetOwnTextReadabilityAsync(IReadOnlyCollection<int> deckIds)
    {
        var blobs = await context.DeckSentenceProfiles.AsNoTracking()
                                 .Where(p => deckIds.Contains(p.DeckId) && p.Profile != null)
                                 .Select(p => new { p.DeckId, p.Profile })
                                 .ToListAsync();
        if (blobs.Count == 0) return [];

        var isKnown = await KnownLookup(blobs.SelectMany(b => SentenceProfileCodec.Decode(b.Profile!).Keys));
        var result = new Dictionary<int, (float, float)>(blobs.Count);
        foreach (var blob in blobs)
        {
            var (total, readable, oneUnknown) = SentenceProfileCodec.CountReadable(blob.Profile!, isKnown);
            if (total > 0) result[blob.DeckId] = (readable * 100f / total, oneUnknown * 100f / total);
        }

        return result;
    }

    public async Task<DeckIPlusOneSentencesResponse?> GetIPlusOneSentencesAsync(int deckId, int offset, string? knownVersion)
    {
        var key = $"iplusone:{currentUser.UserId}:{deckId}:{knownVersion}:{await KnownStamp()}";
        if (!cache.Cache.TryGetValue(key, out (List<IPlusOneEntry> Entries, int Checked) listing))
        {
            var hits = await GetIPlusOneHitsAsync(deckId);
            if (hits == null) return null;

            listing = (OrderByUnlocks(hits.Hits), hits.Checked);
            cache.Cache.Set(key, listing, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = listing.Entries.Count + 1 });
        }

        var page = listing.Entries.Skip(Math.Max(offset, 0)).Take(IPlusOnePageSize).ToList();
        var dtos = (await exampleSentences.BuildDtosAsync(page.Select(e => e.SentenceId).ToList(), 0, 0)).ToDictionary(d => d.SentenceId);
        var words = await LoadWordSummaries(page.Select(e => e.WordKey));

        var sentences = new List<DeckIPlusOneSentenceDto>(page.Count);
        foreach (var entry in page)
        {
            if (!dtos.TryGetValue(entry.SentenceId, out var dto) || !words.TryGetValue(entry.WordKey, out var word)) continue;
            dto.WordPosition = entry.Position;
            dto.WordLength = entry.Length;
            sentences.Add(new DeckIPlusOneSentenceDto { Sentence = dto, Word = word, WordSentences = entry.WordSentences });
        }

        return new DeckIPlusOneSentencesResponse { Sentences = sentences, Total = listing.Entries.Count, Checked = listing.Checked };
    }

    /// <summary>The deck's texts in reading order: its children, or the deck itself when it has none. Null when the deck doesn't exist.</summary>
    private async Task<List<Part>?> LoadParts(int deckId)
    {
        var deck = await context.Decks.AsNoTracking()
                                .Where(d => d.DeckId == deckId)
                                .Select(d => new { d.DeckId, d.OriginalTitle })
                                .FirstOrDefaultAsync();
        if (deck == null) return null;

        var children = await context.Decks.AsNoTracking()
                                    .Where(d => d.ParentDeckId == deckId)
                                    .OrderBy(d => d.DeckOrder).ThenBy(d => d.DeckId)
                                    .Select(d => new { d.DeckId, d.DeckOrder, d.OriginalTitle })
                                    .ToListAsync();

        return children.Count > 0
            ? children.Select((c, i) => new Part(c.DeckId, i + 1, c.OriginalTitle)).ToList()
            : [new Part(deck.DeckId, 1, deck.OriginalTitle)];
    }

    /// <summary>
    /// When the user's known words last changed. Every card, word-set and import change marks coverage dirty at that
    /// moment, so a new stamp means a cached result may be stale; the client's version alone misses changes made on
    /// other pages (marking a word mastered) or devices (an Anki sync).
    /// </summary>
    /// <remarks>The coverage job clears the dirty stamp when it finishes, so its refresh time is part of the stamp too.</remarks>
    private async Task<string> KnownStamp()
    {
        var userId = currentUser.UserId;
        if (userId == null) return "";
        var meta = await userContext.UserMetadatas.AsNoTracking().Where(m => m.UserId == userId)
                                    .Select(m => new { m.CoverageDirtyAt, m.CoverageRefreshedAt }).FirstOrDefaultAsync();
        return $"{meta?.CoverageDirtyAt?.Ticks}.{meta?.CoverageRefreshedAt?.Ticks}";
    }

    private async Task<Func<int, bool>> KnownLookup(IEnumerable<int> wordKeys)
    {
        var forms = wordKeys.Distinct().Select(ExampleSentenceTokens.FromWordKey).ToList();
        var states = await currentUser.GetKnownWordsState(forms);
        var known = states.Where(kv => SentenceComprehension.IsKnown(kv.Value))
                          .Select(kv => ExampleSentenceTokens.WordKey(kv.Key.WordId, kv.Key.ReadingIndex))
                          .ToHashSet();
        return known.Contains;
    }

    private async Task<Dictionary<int, MiningStatus>> MiningStatuses(IEnumerable<int> wordKeys)
    {
        var forms = wordKeys.Distinct().Select(ExampleSentenceTokens.FromWordKey).ToList();
        var states = await currentUser.GetKnownWordsState(forms);
        return states.ToDictionary(kv => ExampleSentenceTokens.WordKey(kv.Key.WordId, kv.Key.ReadingIndex), kv => SentenceComprehension.MiningStatusOf(kv.Value));
    }

    private async Task Fill(DeckSentenceStatsDto result, SentenceStats stats, List<Part> parts, bool summaryOnly)
    {
        result.HasData = stats.Total > 0;
        result.Total = stats.Total;
        result.Readable = stats.ByUnknown[0];
        result.OneUnknown = stats.ByUnknown[1];
        result.TwoUnknown = stats.ByUnknown[2];
        result.ThreeOrMoreUnknown = stats.ByUnknown[3];
        result.UnknownWords = stats.UnknownWords;
        result.Segments = stats.Segments.Select(s => new SentenceSegmentDto
        {
            Index = s.Index,
            FirstPart = parts.Count > 1 ? parts[s.FirstPart].Order : 0,
            LastPart = parts.Count > 1 ? parts[s.LastPart].Order : 0,
            Title = parts.Count > 1 && s.FirstPart == s.LastPart ? parts[s.FirstPart].Title : null,
            Total = s.Total,
            Readable = s.Readable,
            OneUnknown = s.OneUnknown,
            TwoUnknown = s.TwoUnknown
        }).ToList();

        if (summaryOnly) return;

        var words = await LoadWordSummaries(stats.LearnNext.Select(l => l.WordKey));
        result.LearnNext = stats.LearnNext.Where(l => words.ContainsKey(l.WordKey))
                                .Select(l => new SentenceLearnStepDto { Word = words[l.WordKey], Unlocked = l.Unlocked, ReadableAfter = l.ReadableAfter })
                                .ToList();
        result.Projection = stats.Projection;
        result.Milestones = stats.Milestones;
    }

    public Task<IPlusOneListing?> GetIPlusOneHitsAsync(int deckId) => GetMiningHitsAsync(deckId, MiningRule.Default);

    public async Task<IPlusOneListing?> GetMiningHitsAsync(int deckId, MiningRule rule)
    {
        var key = $"iplusone-hits:{currentUser.UserId}:{deckId}:{rule.CacheKey}:{await KnownStamp()}";
        if (cache.Cache.TryGetValue(key, out IPlusOneListing? cached)) return cached;

        var parts = await LoadParts(deckId);
        if (parts == null) return null;

        var listing = await ListIPlusOne(parts.Select(p => p.DeckId).Append(deckId).Distinct().ToList(), rule);
        cache.Cache.Set(key, listing, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = listing.Hits.Count + 1 });
        return listing;
    }

    /// <summary>Each word's easiest few sentences first, words standing alone in the most sentences ahead, then the rest.</summary>
    private static List<IPlusOneEntry> OrderByUnlocks(List<IPlusOneHit> hits) =>
        hits.GroupBy(h => h.WordKey)
            .SelectMany(g => g.OrderBy(h => h.Difficulty).Select((h, rank) => (Hit: h, Rank: rank, Count: g.Count())))
            .OrderBy(h => h.Rank < SentencesPerWordFirst ? 0 : 1)
            .ThenByDescending(h => h.Count)
            .ThenBy(h => h.Hit.WordKey)
            .ThenBy(h => h.Rank)
            .Select(h => new IPlusOneEntry(h.Hit.SentenceId, h.Hit.WordKey, h.Hit.Position, h.Hit.Length, h.Count))
            .ToList();

    /// <summary>
    /// The decks' example sentences that can be mined under the rule; by default, those with exactly one unknown content word
    /// </summary>
    private async Task<IPlusOneListing> ListIPlusOne(List<int> deckIds, MiningRule rule)
    {
        var rows = await context.ExampleSentences.AsNoTracking()
                                .Where(s => deckIds.Contains(s.DeckId!.Value))
                                .OrderBy(s => s.SentenceId)
                                .Take(IPlusOneCandidateCap)
                                .Select(s => new { s.SentenceId, DeckId = s.DeckId!.Value, s.Difficulty, s.Tokens, TextLength = s.Text.Length })
                                .ToListAsync();

        var decoded = rows.Where(r => !ExampleSentenceTokens.IsPartial(r.Tokens))
                          .Select(r => (Row: r, Tokens: ExampleSentenceTokens.Decode(r.Tokens)))
                          .ToList();
        var contentKeys = decoded.SelectMany(d => d.Tokens)
                                 .Where(t => !t.IsFunctionWord && !SentenceComprehension.IsNameEntry(t.WordId))
                                 .Select(t => t.WordKey);
        var statuses = await MiningStatuses(contentKeys);
        var statusOf = (int wordKey) => statuses.GetValueOrDefault(wordKey, MiningStatus.New);

        var hits = new List<IPlusOneHit>();
        foreach (var (row, tokens) in decoded)
        {
            var targets = SentenceComprehension.MiningTargets(tokens, statusOf, rule);
            if (targets.Count == 0) continue;
            var (newCount, learningCount) = rule.CountsWords ? SentenceComprehension.CountByStatus(tokens, statusOf) : (0, 0);
            foreach (var (token, status) in targets)
                hits.Add(new IPlusOneHit(row.SentenceId, row.DeckId, token.WordKey, token.Position, token.Length, row.Difficulty, row.TextLength, status,
                                         (byte)Math.Min(newCount, byte.MaxValue), (byte)Math.Min(learningCount, byte.MaxValue)));
        }

        return new IPlusOneListing(hits, decoded.Count);
    }

    public Task<Dictionary<int, WordSummaryDto>> LoadWordSummariesAsync(IEnumerable<int> wordKeys) => LoadWordSummaries(wordKeys);

    private async Task<Dictionary<int, WordSummaryDto>> LoadWordSummaries(IEnumerable<int> wordKeys)
    {
        var forms = wordKeys.Distinct().Select(ExampleSentenceTokens.FromWordKey).ToList();
        if (forms.Count == 0) return [];

        var wordIds = forms.Select(f => f.WordId).Distinct().ToList();
        var wordForms = await WordFormHelper.LoadWordForms(context, wordIds);
        var definitions = await context.Definitions.AsNoTracking()
                                       .Where(d => wordIds.Contains(d.WordId))
                                       .OrderBy(d => d.WordId).ThenBy(d => d.SenseIndex)
                                       .Select(d => new { d.WordId, d.EnglishMeanings })
                                       .ToListAsync();
        var firstDefinition = definitions.GroupBy(d => d.WordId).ToDictionary(g => g.Key, g => g.First().EnglishMeanings.FirstOrDefault());
        var frequencies = await context.WordFormFrequencies.AsNoTracking()
                                       .Where(f => wordIds.Contains(f.WordId))
                                       .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                       .ToListAsync();
        var rankByForm = frequencies.ToDictionary(f => (f.WordId, f.ReadingIndex), f => f.FrequencyRank);

        var result = new Dictionary<int, WordSummaryDto>(forms.Count);
        foreach (var (wordId, readingIndex) in forms)
        {
            var form = wordForms.GetValueOrDefault((wordId, (short)readingIndex));
            var rank = rankByForm.GetValueOrDefault((wordId, (short)readingIndex));
            result[ExampleSentenceTokens.WordKey(wordId, readingIndex)] = new WordSummaryDto
            {
                WordId = wordId,
                ReadingIndex = readingIndex,
                Reading = form?.Text ?? "",
                ReadingFurigana = form?.RubyText ?? form?.Text ?? "",
                MainDefinition = firstDefinition.GetValueOrDefault(wordId),
                FrequencyRank = rank > 0 ? rank : null
            };
        }

        return result;
    }
}
