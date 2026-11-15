using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <param name="DeckIds">Titles or single episodes; none means every title with sentences.</param>
/// <param name="DictionaryId">The frequency dictionary for ranks; none uses Jiten's own frequency.</param>
/// <param name="Sort">frequency, unlocks (sentences the word is the one unknown in), length, difficulty or order (as the titles run).</param>
/// <param name="Descending">Flips the sort's natural direction (frequency, length, difficulty and order ascend; unlocks descends).</param>
/// <param name="HideUnranked">Leave out words the frequency source doesn't rank.</param>
/// <param name="WordTypes">verb, i-adjective, na-adjective, noun, adverb, expression, other; none means all.</param>
/// <param name="Script">kana (written without kanji) or kanji; none means both.</param>
/// <param name="ExcludeTerms">Words to leave out, matched against every written form of a word (the client sends what's already in Anki).</param>
/// <param name="Statuses">new, learning, young, mature: the target word's status; none means new and learning.</param>
/// <param name="OthersKnownAt">young or mature: how well every other content word must be known; none means young.</param>
/// <param name="Layout">word (one row per word with its best sentences) or sentence.</param>
/// <param name="IncludeTerms">When sent, only words whose form in the sentence, or main form, is among these (the client sends the
/// words of an Anki deck).</param>
/// <param name="UnlearnedMin">Bounds (0 to 5) on the sentence's New content words, the target included. Setting any of the four
/// count bounds lifts the one-weak-word rule; an unset bound is 0 or 5.</param>
/// <param name="LearningMin">The same for Learning (due or suspended) content words.</param>
public record MineRequest(
    List<int>? DeckIds = null,
    int? DictionaryId = null,
    string Sort = "frequency",
    bool Descending = false,
    int? FrequencyMin = null,
    int? FrequencyMax = null,
    bool HideUnranked = false,
    int? LengthMin = null,
    int? LengthMax = null,
    bool RequireEnglish = false,
    bool RequireAudio = false,
    List<string>? WordTypes = null,
    string? Script = null,
    List<string>? ExcludeTerms = null,
    List<string>? Statuses = null,
    string? OthersKnownAt = null,
    string Layout = "word",
    int Offset = 0,
    List<string>? IncludeTerms = null,
    int? UnlearnedMin = null,
    int? UnlearnedMax = null,
    int? LearningMin = null,
    int? LearningMax = null)
{
    public const int MaxCount = 5;

    public bool FiltersCounts => UnlearnedMin != null || UnlearnedMax != null || LearningMin != null || LearningMax != null;
}

/// <param name="Rank">The word's rank in the chosen frequency source.</param>
/// <param name="WordSentences">The word's sentences after the filters.</param>
/// <param name="Status">new, learning, young or mature.</param>
public record MineItemDto(WordSummaryDto Word, int? Rank, string WordType, string Status, int WordSentences, List<ExampleSentenceDto> Sentences);

/// <param name="Total">Rows in the chosen layout (words or sentences).</param>
/// <param name="Checked">Example sentences judged across the titles.</param>
public record MineResponse(List<MineItemDto> Items, int Total, int TotalWords, int TotalSentences, int Checked, string RankSource);

/// <summary>The plan of a bulk run: the first <see cref="MiningSentenceService.MaxRunWords"/> words of a search with their top sentences.</summary>
/// <param name="TotalWords">Words the search returns before the cap.</param>
/// <param name="Sentences">Sentences in the plan.</param>
/// <param name="Capped">The search returned more words than the plan holds.</param>
public record MineRunPlanDto(List<MineItemDto> Items, int TotalWords, int Sentences, bool Capped, string RankSource);

/// <param name="PerWord">Sentences per word, 1 to 5.</param>
public record MineRunPlanRequest(MineRequest Search, int PerWord = 1);

public record MineEpisodeDto(int DeckId, int Order, string Title);

public record MineTitleDto(int DeckId, string Title, string? EnglishTitle, int MediaType, List<MineEpisodeDto> Episodes);

public record MineOptionsDto(List<MineTitleDto> Titles, List<FrequencyDictionaryDto> Dictionaries);

/// <summary>i+1 sentences across titles, sorted and filtered by the target word's frequency, type and script and the sentence's length and media.</summary>
public class MiningSentenceService(
    JitenDbContext context,
    ISentenceStatsService sentenceStats,
    IExampleSentenceQueryService exampleSentences,
    FrequencyDictionaryService dictionaries,
    SentenceFlagService sentenceFlags)
{
    public const int WordPageSize = 20;
    public const int SentencePageSize = 30;
    public const int SentencesPerWord = 5;
    /// <summary>The most words one bulk run takes (GDR-059).</summary>
    public const int MaxRunWords = 500;

    public static readonly string[] WordTypes = ["verb", "i-adjective", "na-adjective", "noun", "adverb", "expression", "other"];

    private sealed record WordInfo(int? Rank, string WordType, bool IsKana, bool Excluded, bool Included);

    public async Task<MineOptionsDto> OptionsAsync(CancellationToken ct)
    {
        var titleIds = await TitlesWithSentences(ct);
        var titles = await context.Decks.AsNoTracking().Where(d => titleIds.Contains(d.DeckId))
                                  .Select(d => new { d.DeckId, d.OriginalTitle, d.EnglishTitle, d.RomajiTitle, d.MediaType })
                                  .ToListAsync(ct);
        var episodes = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId != null && titleIds.Contains(d.ParentDeckId.Value))
                                    .OrderBy(d => d.DeckOrder).ThenBy(d => d.DeckId)
                                    .Select(d => new { d.DeckId, ParentDeckId = d.ParentDeckId!.Value, d.OriginalTitle })
                                    .ToListAsync(ct);
        var byTitle = episodes.GroupBy(e => e.ParentDeckId).ToDictionary(g => g.Key, g => g.Select((e, i) => new MineEpisodeDto(e.DeckId, i + 1, e.OriginalTitle)).ToList());

        return new MineOptionsDto(
            titles.OrderBy(t => t.EnglishTitle ?? t.RomajiTitle ?? t.OriginalTitle)
                  .Select(t => new MineTitleDto(t.DeckId, t.OriginalTitle, t.EnglishTitle ?? t.RomajiTitle, (int)t.MediaType, byTitle.GetValueOrDefault(t.DeckId) ?? []))
                  .ToList(),
            await dictionaries.ListAsync(ct));
    }

    public async Task<MineResponse> SearchAsync(string userId, MineRequest request, CancellationToken ct)
    {
        var found = await FilterAsync(userId, request, ct);
        var offset = Math.Max(request.Offset, 0);

        List<(int WordKey, List<IPlusOneHit> Sentences)> page;
        int total;
        if (request.Layout == "sentence")
        {
            var ordered = Order(found.Hits, h => SortKey(request.Sort, found.Words[h.WordKey].Rank, found.ByWord[h.WordKey].Count, h.TextLength, h.Difficulty, h.SentenceId), request);
            total = ordered.Count;
            page = ordered.Skip(offset).Take(SentencePageSize).Select(h => (h.WordKey, new List<IPlusOneHit> { h })).ToList();
        }
        else
        {
            var ordered = OrderWords(found, request);
            total = ordered.Count;
            page = ordered.Skip(offset).Take(WordPageSize).Select(g => (g.Key, g.Value.Take(SentencesPerWord).ToList())).ToList();
        }

        var items = await BuildItems(page, found, request);
        return new MineResponse(items, total, found.ByWord.Count, found.Hits.Count, found.Checked, found.RankSource);
    }

    /// <summary>
    /// Every word of the search in its sort order, up to <see cref="MaxRunWords"/>, each with its first <paramref name="perWord"/>
    /// sentences in By-word order: the plan of a bulk pipeline run (GDR-059). Layout and offset are ignored.
    /// </summary>
    public async Task<MineRunPlanDto> AllAsync(string userId, MineRequest request, int perWord, CancellationToken ct)
    {
        perWord = Math.Clamp(perWord, 1, SentencesPerWord);
        var found = await FilterAsync(userId, request, ct);
        var ordered = OrderWords(found, request);
        var page = ordered.Take(MaxRunWords).Select(g => (g.Key, g.Value.Take(perWord).ToList())).ToList();
        var items = await BuildItems(page, found, request);
        return new MineRunPlanDto(items, found.ByWord.Count, items.Sum(i => i.Sentences.Count), ordered.Count > MaxRunWords, found.RankSource);
    }

    private sealed record Found(
        List<IPlusOneHit> Hits,
        Dictionary<int, WordInfo> Words,
        Dictionary<int, List<IPlusOneHit>> ByWord,
        int Checked,
        string RankSource);

    /// <summary>The search's hits after every filter, and each word's hits easiest first, then shortest.</summary>
    private async Task<Found> FilterAsync(string userId, MineRequest request, CancellationToken ct)
    {
        var (hits, checkedCount) = await CollectHits(request.DeckIds, RuleOf(request), ct);

        var flagged = await sentenceFlags.OpenSentenceIdsAsync(userId, ct);
        hits = hits.Where(h => !flagged.Contains(h.SentenceId)).ToList();

        var deckIds = hits.Select(h => h.DeckId).Distinct().ToList();
        if (request.RequireAudio)
        {
            var withVideo = (await context.DeckVideoSources.AsNoTracking().Where(v => deckIds.Contains(v.DeckId)).Select(v => v.DeckId).ToListAsync(ct)).ToHashSet();
            hits = hits.Where(h => withVideo.Contains(h.DeckId)).ToList();
        }

        if (request.RequireEnglish)
        {
            var withTrack = (await context.DeckEnglishTracks.AsNoTracking().Where(t => deckIds.Contains(t.DeckId)).Select(t => t.DeckId).ToListAsync(ct)).ToHashSet();
            var translated = (await context.ExampleSentences.AsNoTracking()
                                           .Where(s => deckIds.Contains(s.DeckId!.Value)
                                                       && context.ExampleSentenceTranslations.Any(t => t.CentralSentenceId == s.CentralSentenceId))
                                           .Select(s => s.SentenceId).ToListAsync(ct)).ToHashSet();
            hits = hits.Where(h => withTrack.Contains(h.DeckId) || translated.Contains(h.SentenceId)).ToList();
        }

        if (request.FiltersCounts)
        {
            int newMin = request.UnlearnedMin ?? 0, newMax = request.UnlearnedMax ?? MineRequest.MaxCount;
            int learningMin = request.LearningMin ?? 0, learningMax = request.LearningMax ?? MineRequest.MaxCount;
            hits = hits.Where(h => h.NewCount >= newMin && h.NewCount <= newMax && h.LearningCount >= learningMin && h.LearningCount <= learningMax).ToList();
        }

        if (request.LengthMin is { } minLength) hits = hits.Where(h => h.TextLength >= minLength).ToList();
        if (request.LengthMax is { } maxLength) hits = hits.Where(h => h.TextLength <= maxLength).ToList();

        var (words, rankSource) = await DescribeWords(hits.Select(h => h.WordKey).Distinct().ToList(), request, ct);
        var types = request.WordTypes is { Count: > 0 } wanted ? wanted.ToHashSet() : null;
        hits = hits.Where(h =>
        {
            var w = words[h.WordKey];
            if (w.Excluded || !w.Included) return false;
            if (types != null && !types.Contains(w.WordType)) return false;
            if (request.Script == "kana" && !w.IsKana || request.Script == "kanji" && w.IsKana) return false;
            if (w.Rank == null) return !request.HideUnranked && request.FrequencyMin == null && request.FrequencyMax == null;
            return w.Rank >= (request.FrequencyMin ?? 0) && w.Rank <= (request.FrequencyMax ?? int.MaxValue);
        }).ToList();

        var byWord = hits.GroupBy(h => h.WordKey).ToDictionary(g => g.Key, g => g.OrderBy(h => h.Difficulty).ThenBy(h => h.TextLength).ToList());
        return new Found(hits, words, byWord, checkedCount, rankSource);
    }

    private static List<KeyValuePair<int, List<IPlusOneHit>>> OrderWords(Found found, MineRequest request) =>
        Order(found.ByWord.ToList(), g =>
        {
            var first = g.Value[0];
            return SortKey(request.Sort, found.Words[g.Key].Rank, g.Value.Count, g.Value.Min(h => h.TextLength), first.Difficulty, g.Value.Min(h => h.SentenceId));
        }, request);

    private async Task<List<MineItemDto>> BuildItems(List<(int WordKey, List<IPlusOneHit> Sentences)> page, Found found, MineRequest request)
    {
        var pageHits = page.SelectMany(p => p.Sentences).ToList();
        var dtos = (await exampleSentences.BuildDtosAsync(pageHits.Select(h => h.SentenceId).Distinct().ToList(), 0, 0)).ToDictionary(d => d.SentenceId);
        var summaries = await sentenceStats.LoadWordSummariesAsync(page.Select(p => p.WordKey));

        var items = new List<MineItemDto>(page.Count);
        foreach (var (wordKey, sentences) in page)
        {
            if (!summaries.TryGetValue(wordKey, out var summary)) continue;
            var sentenceDtos = new List<ExampleSentenceDto>(sentences.Count);
            foreach (var hit in sentences)
            {
                if (!dtos.TryGetValue(hit.SentenceId, out var dto)) continue;
                // A sentence can be on the page for two words, so each gets its own copy
                var copy = dto.ForWord(hit.Position, hit.Length);
                if (request.FiltersCounts)
                {
                    copy.NewCount = hit.NewCount;
                    copy.LearningCount = hit.LearningCount;
                }

                sentenceDtos.Add(copy);
            }

            if (sentenceDtos.Count > 0)
                items.Add(new MineItemDto(summary, found.Words[wordKey].Rank, found.Words[wordKey].WordType, sentences[0].Status.ToString().ToLowerInvariant(), found.ByWord[wordKey].Count, sentenceDtos));
        }

        return items;
    }

    private static MiningRule RuleOf(MineRequest request)
    {
        var targets = (request.Statuses ?? [])
                      .Select(s => Enum.TryParse<MiningStatus>(s, ignoreCase: true, out var status) && status != MiningStatus.Settled ? status : (MiningStatus?)null)
                      .OfType<MiningStatus>()
                      .ToHashSet();
        // Only as many weak words as the bounds allow, so a tight filter caches a small listing; at least 2, so the hits carry counts.
        var maxWeak = request.FiltersCounts
            ? Math.Clamp((request.UnlearnedMax ?? MineRequest.MaxCount) + (request.LearningMax ?? MineRequest.MaxCount), 2, MiningRule.LooseMaxWeak)
            : 1;
        return new MiningRule(targets.Count > 0 ? targets : MiningRule.Default.Targets, request.OthersKnownAt == "mature", maxWeak);
    }

    /// <summary>Unranked words sort after ranked ones in either direction.</summary>
    private static (bool Missing, double Value, long Tie) SortKey(string sort, int? rank, int sentences, int length, float difficulty, long sentenceId) =>
        sort switch
        {
            "unlocks" => (false, sentences, sentenceId),
            "length" => (false, length, sentenceId),
            "difficulty" => (false, difficulty, sentenceId),
            "order" => (false, sentenceId, sentenceId),
            _ => (rank == null, rank ?? 0, sentenceId)
        };

    private static List<T> Order<T>(IEnumerable<T> items, Func<T, (bool Missing, double Value, long Tie)> key, MineRequest request)
    {
        // Unlocks naturally runs most first; everything else least first.
        var descending = request.Sort == "unlocks" ? !request.Descending : request.Descending;
        var keyed = items.Select(i => (Item: i, Key: key(i))).OrderBy(k => k.Key.Missing);
        var sorted = descending ? keyed.ThenByDescending(k => k.Key.Value) : keyed.ThenBy(k => k.Key.Value);
        return sorted.ThenBy(k => k.Key.Tie).Select(k => k.Item).ToList();
    }

    /// <summary>The cached i+1 hits of each selected title, narrowed to the selected episodes.</summary>
    private async Task<(List<IPlusOneHit> Hits, int Checked)> CollectHits(List<int>? deckIds, MiningRule rule, CancellationToken ct)
    {
        Dictionary<int, HashSet<int>?> wanted; // title -> episodes (null = whole title)
        if (deckIds is { Count: > 0 })
        {
            var decks = await context.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId))
                                     .Select(d => new { d.DeckId, d.ParentDeckId }).ToListAsync(ct);
            wanted = new();
            foreach (var deck in decks.Where(d => d.ParentDeckId == null))
                wanted[deck.DeckId] = null;
            foreach (var episode in decks.Where(d => d.ParentDeckId != null))
            {
                var title = episode.ParentDeckId!.Value;
                if (wanted.TryGetValue(title, out var set) && set == null) continue;
                (wanted[title] = set ?? []).Add(episode.DeckId);
            }
        }
        else
        {
            wanted = (await TitlesWithSentences(ct)).ToDictionary(id => id, _ => (HashSet<int>?)null);
        }

        var hits = new List<IPlusOneHit>();
        var checkedCount = 0;
        foreach (var (titleId, episodes) in wanted)
        {
            var listing = await sentenceStats.GetMiningHitsAsync(titleId, rule);
            if (listing == null) continue;
            checkedCount += listing.Checked;
            hits.AddRange(episodes == null ? listing.Hits : listing.Hits.Where(h => episodes.Contains(h.DeckId)));
        }

        return (hits, checkedCount);
    }

    /// <summary>Top-level decks whose text (or whose episodes' text) has example sentences.</summary>
    private async Task<List<int>> TitlesWithSentences(CancellationToken ct)
    {
        var deckIds = await context.ExampleSentences.AsNoTracking().Select(s => s.DeckId).Distinct().ToListAsync(ct);
        return await context.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId))
                            .Select(d => d.ParentDeckId ?? d.DeckId).Distinct().ToListAsync(ct);
    }

    private async Task<(Dictionary<int, WordInfo> Words, string RankSource)> DescribeWords(List<int> wordKeys, MineRequest request, CancellationToken ct)
    {
        var forms = wordKeys.Select(k => (Key: k, Form: ExampleSentenceTokens.FromWordKey(k))).ToList();
        var wordIds = forms.Select(f => f.Form.WordId).Distinct().ToList();

        Dictionary<(int WordId, short ReadingIndex), int> ranks;
        string rankSource;
        var dictionary = request.DictionaryId is { } id ? await context.FrequencyDictionaries.AsNoTracking().FirstOrDefaultAsync(d => d.FrequencyDictionaryId == id, ct) : null;
        if (dictionary != null)
        {
            ranks = await dictionaries.RanksAsync(dictionary.FrequencyDictionaryId, wordIds, ct);
            rankSource = dictionary.Title;
        }
        else
        {
            ranks = (await context.WordFormFrequencies.AsNoTracking().Where(f => wordIds.Contains(f.WordId))
                                  .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank }).ToListAsync(ct))
                    .Where(f => f.FrequencyRank > 0)
                    .ToDictionary(f => (f.WordId, f.ReadingIndex), f => f.FrequencyRank);
            rankSource = "Jiten";
        }

        var partsOfSpeech = await context.JMDictWords.AsNoTracking().Where(w => wordIds.Contains(w.WordId))
                                         .Select(w => new { w.WordId, w.PartsOfSpeech }).ToDictionaryAsync(w => w.WordId, w => w.PartsOfSpeech, ct);
        var wordForms = await context.WordForms.AsNoTracking().Where(f => wordIds.Contains(f.WordId))
                                     .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType }).ToListAsync(ct);
        var formType = wordForms.ToDictionary(f => (f.WordId, f.ReadingIndex), f => f.FormType);
        var formText = wordForms.ToDictionary(f => (f.WordId, f.ReadingIndex), f => f.Text);
        var excluded = WordsWithAFormIn(request.ExcludeTerms) ?? [];
        var includeTerms = TermSet(request.IncludeTerms);

        var words = forms.ToDictionary(f => f.Key, f =>
        {
            var key = (f.Form.WordId, (short)f.Form.ReadingIndex);
            return new WordInfo(ranks.TryGetValue(key, out var rank) ? rank : null,
                                ClassifyWordType(partsOfSpeech.GetValueOrDefault(f.Form.WordId) ?? []),
                                formType.GetValueOrDefault(key) == JmDictFormType.KanaForm,
                                excluded.Contains(f.Form.WordId),
                                includeTerms == null || IsInTerms(includeTerms, formText.GetValueOrDefault(key), formText.GetValueOrDefault((f.Form.WordId, (short)0))));
        });
        return (words, rankSource);

        HashSet<int>? WordsWithAFormIn(List<string>? terms)
        {
            var termSet = TermSet(terms);
            return termSet == null ? null : wordForms.Where(f => termSet.Contains(f.Text)).Select(f => f.WordId).ToHashSet();
        }
    }

    private static HashSet<string>? TermSet(List<string>? terms) =>
        terms?.Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet();

    /// <summary>
    /// Whether a sentence's word is among a deck's words: the deck has the form the sentence uses, or the word's main
    /// form. Any other form isn't enough, since JMDict lists other words' spellings as rare forms (本 is a form of 元 もと,
    /// so a deck with 本 ほん would otherwise take in 元).
    /// </summary>
    public static bool IsInTerms(IReadOnlySet<string> terms, string? usedForm, string? mainForm) =>
        usedForm != null && terms.Contains(usedForm) || mainForm != null && terms.Contains(mainForm);

    /// <summary>A coarse word type from JMDict part-of-speech tags, checked in this order (a suru-verb noun counts as a noun).</summary>
    public static string ClassifyWordType(IReadOnlyCollection<string> tags)
    {
        if (tags.Any(t => t is "v1" or "v1-s" or "vk" or "vz" or "vs-i" or "vs-s" or "vs-c" || t.StartsWith("v5") || t.StartsWith("v2") || t.StartsWith("v4")))
            return "verb";
        if (tags.Any(t => t is "adj-i" or "adj-ix")) return "i-adjective";
        if (tags.Any(t => t is "adj-na")) return "na-adjective";
        if (tags.Any(t => t is "n" or "pn" or "adj-no" or "vs" || t.StartsWith("n-"))) return "noun";
        if (tags.Any(t => t.StartsWith("adv"))) return "adverb";
        if (tags.Contains("exp")) return "expression";
        return "other";
    }
}
