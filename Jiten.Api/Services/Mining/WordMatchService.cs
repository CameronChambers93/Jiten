using System.Net;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <param name="Text">The note's fields, any markup; null when only the word and reading are known.</param>
public record WordMatchRequest(string Word, string Reading, string? Text);

/// <param name="Status">One of <see cref="MinedWordMatch"/>'s automatic statuses.</param>
public record WordMatchResult(int WordId, byte ReadingIndex, string Status);

public record WordMatchBatchRequest(List<WordMatchRequest> Words);

/// <param name="Forms">How the entry is written, kanji forms first, so entries with the same English can be told apart.</param>
public record WordCandidateDto(int WordId, byte ReadingIndex, List<string> Glosses, List<string> Forms);

/// <summary>
/// Which JMDict entry an Anki note's word is. The word and reading narrow it to candidates, as the direct lookup does;
/// when several are left, the note's own text decides: the entry whose glosses appear in it, else the most common one,
/// marked guessed. Works on every field of the note, so it doesn't depend on the note type.
/// </summary>
public partial class WordMatchService(IDbContextFactory<JitenDbContext> contextFactory)
{
    public const int MaxTextLength = 20_000;
    public const int MinGlossLength = 3;

    /// <summary>The match for each request, in order; null where the word and reading have no entry.</summary>
    public async Task<List<WordMatchResult?>> MatchAsync(IReadOnlyList<WordMatchRequest> requests, CancellationToken ct)
    {
        var pairs = requests.Select(r => (Word: r.Word.Trim(), Reading: r.Reading.Trim())).Where(p => p.Word.Length > 0).Distinct().ToList();
        var candidates = pairs.Count == 0 ? [] : await Parser.Parser.GetDirectLookupCandidatesByReading(contextFactory, pairs);
        var glosses = await GlossesAsync(candidates.Values.Where(c => c.Count > 1).SelectMany(c => c.Select(x => x.WordId)), ct);

        return requests.Select(r =>
        {
            if (!candidates.TryGetValue((r.Word.Trim(), r.Reading.Trim()), out var found)) return null;
            var list = WithoutNames(found);
            if (list.Count == 1) return new WordMatchResult(list[0].WordId, list[0].ReadingIndex, MinedWordMatch.Single);

            var pick = Decide(list.Select(c => (IReadOnlyList<string>)glosses.GetValueOrDefault(c.WordId, [])).ToList(), r.Text);
            var chosen = list[pick ?? 0];
            return new WordMatchResult(chosen.WordId, chosen.ReadingIndex, pick == null ? MinedWordMatch.Guessed : MinedWordMatch.Gloss);
        }).ToList();
    }

    /// <summary>The entries a note's word could be, most common first, with their glosses.</summary>
    public async Task<List<WordCandidateDto>> CandidatesAsync(string word, string reading, CancellationToken ct)
    {
        var key = (word.Trim(), reading.Trim());
        var candidates = await Parser.Parser.GetDirectLookupCandidatesByReading(contextFactory, [key]);
        if (!candidates.TryGetValue(key, out var found)) return [];
        var list = WithoutNames(found);
        var ids = list.Select(c => c.WordId).ToList();
        var glosses = await GlossesAsync(ids, ct);
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var forms = (await context.WordForms.AsNoTracking().Where(f => ids.Contains(f.WordId))
                                  .OrderBy(f => f.FormType).ThenBy(f => f.ReadingIndex)
                                  .Select(f => new { f.WordId, f.Text }).ToListAsync(ct))
                    .GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.Select(f => f.Text).Distinct().ToList());
        return list.Select(c => new WordCandidateDto(c.WordId, c.ReadingIndex, glosses.GetValueOrDefault(c.WordId, []), forms.GetValueOrDefault(c.WordId, [])))
                   .ToList();
    }

    /// <summary>Name-only entries (JMnedict) are left out, unless they are all there is.</summary>
    private static List<(int WordId, byte ReadingIndex)> WithoutNames(List<(int WordId, byte ReadingIndex)> candidates)
    {
        var words = candidates.Where(c => !SentenceComprehension.IsNameEntry(c.WordId)).ToList();
        return words.Count > 0 ? words : candidates;
    }

    private async Task<Dictionary<int, List<string>>> GlossesAsync(IEnumerable<int> wordIds, CancellationToken ct)
    {
        var ids = wordIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var rows = await context.Definitions.AsNoTracking().Where(d => ids.Contains(d.WordId))
                                .OrderBy(d => d.SenseIndex).ThenBy(d => d.DefinitionId)
                                .Select(d => new { d.WordId, d.EnglishMeanings }).ToListAsync(ct);
        return rows.GroupBy(r => r.WordId).ToDictionary(g => g.Key, g => g.SelectMany(r => r.EnglishMeanings).Where(m => m.Length > 0).Distinct().ToList());
    }

    /// <summary>
    /// The index of the one candidate whose glosses appear in the text as whole words, ignoring glosses every candidate
    /// has; null when none or several do, or there is no text.
    /// </summary>
    public static int? Decide(IReadOnlyList<IReadOnlyList<string>> glossesByCandidate, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalised = $" {NormaliseText(text)} ";

        // Glosses under three letters ("go", "be") turn up in any English sentence on the note, so they don't decide.
        var keys = glossesByCandidate.Select(g => g.Select(NormaliseGloss).Where(k => k.Length >= MinGlossLength).ToHashSet()).ToList();
        var shared = keys.Skip(1).Aggregate(new HashSet<string>(keys.FirstOrDefault() ?? []), (acc, k) =>
        {
            acc.IntersectWith(k);
            return acc;
        });

        int? found = null;
        for (var i = 0; i < keys.Count; i++)
        {
            if (!keys[i].Any(k => !shared.Contains(k) && normalised.Contains($" {k} ", StringComparison.Ordinal))) continue;
            if (found != null) return null;
            found = i;
        }

        return found;
    }

    /// <summary>A note's fields as lower-case words: markup, sound tags and furigana brackets gone, punctuation as spaces.</summary>
    public static string NormaliseText(string text)
    {
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        text = SoundTag().Replace(text, " ");
        text = Markup().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = Furigana().Replace(text, " ");
        return Words(text.ToLowerInvariant());
    }

    /// <summary>A gloss as matched: without parenthesised notes and a leading "to ", lower-case words.</summary>
    public static string NormaliseGloss(string gloss)
    {
        var g = Words(Parenthesised().Replace(gloss, " ").ToLowerInvariant());
        return g.StartsWith("to ", StringComparison.Ordinal) ? g[3..] : g;
    }

    private static string Words(string text) => NonWord().Replace(text, " ").Trim();

    [GeneratedRegex(@"\[sound:[^\]]*\]")]
    private static partial Regex SoundTag();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Furigana();

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthesised();

    [GeneratedRegex(@"[^\p{L}\p{N}']+")]
    private static partial Regex NonWord();
}
