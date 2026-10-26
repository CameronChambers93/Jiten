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
/// <param name="Layout">word (one row per word with its best sentences) or sentence.</param>
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
    string Layout = "word",
    int Offset = 0);

/// <param name="Rank">The word's rank in the chosen frequency source.</param>
/// <param name="WordSentences">The word's sentences after the filters.</param>
public record MineItemDto(WordSummaryDto Word, int? Rank, string WordType, int WordSentences, List<ExampleSentenceDto> Sentences);

/// <param name="Total">Rows in the chosen layout (words or sentences).</param>
/// <param name="Checked">Example sentences judged across the titles.</param>
public record MineResponse(List<MineItemDto> Items, int Total, int TotalWords, int TotalSentences, int Checked, string RankSource);

public record MineEpisodeDto(int DeckId, int Order, string Title);

public record MineTitleDto(int DeckId, string Title, string? EnglishTitle, int MediaType, List<MineEpisodeDto> Episodes);

public record MineOptionsDto(List<MineTitleDto> Titles, List<FrequencyDictionaryDto> Dictionaries);

/// <summary>i+1 sentences across titles, sorted and filtered by the target word's frequency, type and script and the sentence's length and media.</summary>
public class MiningSentenceService(
    JitenDbContext context,
    ISentenceStatsService sentenceStats,
    IExampleSentenceQueryService exampleSentences,
    FrequencyDictionaryService dictionaries)
{
    public const int WordPageSize = 20;
    public const int SentencePageSize = 30;
    public const int SentencesPerWord = 5;

    public static readonly string[] WordTypes = ["verb", "i-adjective", "na-adjective", "noun", "adverb", "expression", "other"];

    private sealed record WordInfo(int? Rank, string WordType, bool IsKana, bool Excluded);

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

    public async Task<MineResponse> SearchAsync(MineRequest request, CancellationToken ct)
    {
        var (hits, checkedCount) = await CollectHits(request.DeckIds, ct);

        var deckIds = hits.Select(h => h.DeckId).Distinct().ToList();
        if (request.RequireAudio)
        {
            var withVideo = (await context.DeckVideoSources.AsNoTracking().Where(v => deckIds.Contains(v.DeckId)).Select(v => v.DeckId).ToListAsync(ct)).ToHashSet();
            hits = hits.Where(h => withVideo.Contains(h.DeckId)).ToList();
        }

        if (request.RequireEnglish)
        {
            var withTrack = (await context.DeckEnglishTracks.AsNoTracking().Where(t => deckIds.Contains(t.DeckId)).Select(t => t.DeckId).ToListAsync(ct)).ToHashSet();
            var translated = (await context.ExampleSentenceTranslations.AsNoTracking().Where(t => deckIds.Contains(t.DeckId)).Select(t => t.SentenceId)
                                           .ToListAsync(ct)).ToHashSet();
            hits = hits.Where(h => withTrack.Contains(h.DeckId) || translated.Contains(h.SentenceId)).ToList();
        }

        if (request.LengthMin is { } minLength) hits = hits.Where(h => h.TextLength >= minLength).ToList();
        if (request.LengthMax is { } maxLength) hits = hits.Where(h => h.TextLength <= maxLength).ToList();

        var (words, rankSource) = await DescribeWords(hits.Select(h => h.WordKey).Distinct().ToList(), request, ct);
        var types = request.WordTypes is { Count: > 0 } wanted ? wanted.ToHashSet() : null;
        hits = hits.Where(h =>
        {
            var w = words[h.WordKey];
            if (w.Excluded) return false;
            if (types != null && !types.Contains(w.WordType)) return false;
            if (request.Script == "kana" && !w.IsKana || request.Script == "kanji" && w.IsKana) return false;
            if (w.Rank == null) return !request.HideUnranked && request.FrequencyMin == null && request.FrequencyMax == null;
            return w.Rank >= (request.FrequencyMin ?? 0) && w.Rank <= (request.FrequencyMax ?? int.MaxValue);
        }).ToList();

        var byWord = hits.GroupBy(h => h.WordKey).ToDictionary(g => g.Key, g => g.OrderBy(h => h.Difficulty).ThenBy(h => h.TextLength).ToList());
        var offset = Math.Max(request.Offset, 0);

        List<(int WordKey, List<IPlusOneHit> Sentences)> page;
        int total;
        if (request.Layout == "sentence")
        {
            var ordered = Order(hits, h => SortKey(request.Sort, words[h.WordKey].Rank, byWord[h.WordKey].Count, h.TextLength, h.Difficulty, h.SentenceId), request);
            total = ordered.Count;
            page = ordered.Skip(offset).Take(SentencePageSize).Select(h => (h.WordKey, new List<IPlusOneHit> { h })).ToList();
        }
        else
        {
            var ordered = Order(byWord.ToList(), g =>
            {
                var first = g.Value[0];
                return SortKey(request.Sort, words[g.Key].Rank, g.Value.Count, g.Value.Min(h => h.TextLength), first.Difficulty, g.Value.Min(h => h.SentenceId));
            }, request);
            total = ordered.Count;
            page = ordered.Skip(offset).Take(WordPageSize).Select(g => (g.Key, g.Value.Take(SentencesPerWord).ToList())).ToList();
        }

        var pageHits = page.SelectMany(p => p.Sentences).ToList();
        var dtos = (await exampleSentences.BuildDtosAsync(pageHits.Select(h => h.SentenceId).ToList(), 0, 0)).ToDictionary(d => d.SentenceId);
        var summaries = await sentenceStats.LoadWordSummariesAsync(page.Select(p => p.WordKey));

        var items = new List<MineItemDto>(page.Count);
        foreach (var (wordKey, sentences) in page)
        {
            if (!summaries.TryGetValue(wordKey, out var summary)) continue;
            var sentenceDtos = new List<ExampleSentenceDto>(sentences.Count);
            foreach (var hit in sentences)
            {
                if (!dtos.TryGetValue(hit.SentenceId, out var dto)) continue;
                dto.WordPosition = hit.Position;
                dto.WordLength = hit.Length;
                sentenceDtos.Add(dto);
            }

            if (sentenceDtos.Count > 0)
                items.Add(new MineItemDto(summary, words[wordKey].Rank, words[wordKey].WordType, byWord[wordKey].Count, sentenceDtos));
        }

        return new MineResponse(items, total, byWord.Count, hits.Count, checkedCount, rankSource);
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
    private async Task<(List<IPlusOneHit> Hits, int Checked)> CollectHits(List<int>? deckIds, CancellationToken ct)
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
            var listing = await sentenceStats.GetIPlusOneHitsAsync(titleId);
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
        var excluded = new HashSet<int>();
        if (request.ExcludeTerms is { Count: > 0 } terms)
        {
            var termSet = terms.Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet();
            excluded = wordForms.Where(f => termSet.Contains(f.Text)).Select(f => f.WordId).ToHashSet();
        }

        var words = forms.ToDictionary(f => f.Key, f =>
        {
            var key = (f.Form.WordId, (short)f.Form.ReadingIndex);
            return new WordInfo(ranks.TryGetValue(key, out var rank) ? rank : null,
                                ClassifyWordType(partsOfSpeech.GetValueOrDefault(f.Form.WordId) ?? []),
                                formType.GetValueOrDefault(key) == JmDictFormType.KanaForm,
                                excluded.Contains(f.Form.WordId));
        });
        return (words, rankSource);
    }

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
