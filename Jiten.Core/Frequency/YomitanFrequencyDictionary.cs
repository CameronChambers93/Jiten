using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Core.Data.JMDict;
using WanaKanaShaapu;

namespace Jiten.Core.Frequency;

/// <summary>One frequency entry of a Yomitan dictionary: the term, the reading it's for (when given) and its rank.</summary>
public record YomitanFrequencyEntry(string Term, string? Reading, int Value);

public record YomitanFrequencyDictionaryFile(string Title, string? Revision, List<YomitanFrequencyEntry> Entries);

/// <summary>A word form to rank: its text, its ruby (kanji forms) and whether it's written in kana.</summary>
public record FrequencyForm(int WordId, short ReadingIndex, string Text, string RubyText, JmDictFormType FormType);

/// <summary>
/// Reads Yomitan frequency dictionaries (the zip's index.json and term_meta_bank_*.json) and ranks Jiten word forms by
/// them the way Yomitan would show the rank for that headword and reading, so mining can sort by the same number
/// Yomitan writes to a card's frequency field.
/// </summary>
public static partial class YomitanFrequencyDictionary
{
    public static YomitanFrequencyDictionaryFile Read(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var indexEntry = archive.GetEntry("index.json") ?? throw new InvalidDataException("Not a Yomitan dictionary: no index.json.");
        string title;
        string? revision, mode;
        using (var index = JsonDocument.Parse(indexEntry.Open()))
        {
            title = index.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            revision = index.RootElement.TryGetProperty("revision", out var r) ? r.GetString() : null;
            mode = index.RootElement.TryGetProperty("frequencyMode", out var m) ? m.GetString() : null;
        }

        var entries = new List<YomitanFrequencyEntry>();
        foreach (var bank in archive.Entries.Where(e => TermMetaBank().IsMatch(e.FullName)))
        {
            using var doc = JsonDocument.Parse(bank.Open());
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.GetArrayLength() < 3 || row[1].GetString() != "freq") continue;
                var entry = ParseEntry(row[0].GetString() ?? "", row[2]);
                if (entry != null) entries.Add(entry);
            }
        }

        if (entries.Count == 0) throw new InvalidDataException("The dictionary has no frequency entries.");

        // Occurrence-based dictionaries count uses (higher is more frequent); turn the counts into ranks.
        if (mode == "occurrence-based")
        {
            var ranks = entries.Select(e => e.Value).Distinct().OrderDescending().Select((v, i) => (v, i + 1)).ToDictionary(x => x.v, x => x.Item2);
            entries = entries.Select(e => e with { Value = ranks[e.Value] }).ToList();
        }

        return new YomitanFrequencyDictionaryFile(title, revision, entries);
    }

    /// <summary>Data is a number, {value, displayValue}, or {reading, frequency} with either of those inside.</summary>
    private static YomitanFrequencyEntry? ParseEntry(string term, JsonElement data)
    {
        string? reading = null;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reading", out var r))
        {
            reading = r.GetString();
            if (!data.TryGetProperty("frequency", out data)) return null;
        }

        int? value = data.ValueKind switch
        {
            JsonValueKind.Number => data.TryGetInt32(out var n) ? n : (int)data.GetDouble(),
            JsonValueKind.String => int.TryParse(data.GetString(), out var n) ? n : null,
            JsonValueKind.Object when data.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number => (int)v.GetDouble(),
            _ => null
        };
        return value is > 0 && term.Length > 0 ? new YomitanFrequencyEntry(term, reading, value.Value) : null;
    }

    /// <summary>
    /// Each form's rank: the lowest entry for its text whose reading matches (or that gives none). A kana form also takes
    /// the entries of its word's kanji forms that carry its reading, as Yomitan shows 思う's ranks on おもう.
    /// </summary>
    public static Dictionary<(int WordId, short ReadingIndex), int> Rank(IEnumerable<YomitanFrequencyEntry> entries, IEnumerable<FrequencyForm> forms)
    {
        var byTerm = entries.GroupBy(e => e.Term)
                            .ToDictionary(g => g.Key, g => g.Select(e => (Reading: e.Reading == null ? null : Hiragana(e.Reading), e.Value)).ToList());

        var ranks = new Dictionary<(int, short), int>();
        foreach (var word in forms.GroupBy(f => f.WordId))
        {
            var kanjiForms = word.Where(f => f.FormType == JmDictFormType.KanjiForm).ToList();
            foreach (var form in word)
            {
                int? best = null;
                void Take(string term, string reading, bool readingRequired)
                {
                    if (!byTerm.TryGetValue(term, out var candidates)) return;
                    foreach (var (r, value) in candidates)
                        if ((r == null ? !readingRequired : r == reading) && (best == null || value < best))
                            best = value;
                }

                if (form.FormType == JmDictFormType.KanjiForm)
                {
                    Take(form.Text, Hiragana(RubyReading(form.RubyText)), false);
                }
                else
                {
                    var reading = Hiragana(form.Text);
                    Take(form.Text, reading, false);
                    foreach (var kanji in kanjiForms)
                        Take(kanji.Text, reading, true);
                }

                if (best != null) ranks[(form.WordId, form.ReadingIndex)] = best.Value;
            }
        }

        return ranks;
    }

    /// <summary>The kana reading of ruby text like "決[き]まる" or "Ｔ[ティー]シャツ".</summary>
    public static string RubyReading(string ruby) => RubyBase().Replace(ruby, "$1").Replace(" ", "");

    private static string Hiragana(string text) => WanaKana.ToHiragana(text);

    [GeneratedRegex(@"^term_meta_bank_\d+\.json$")]
    private static partial Regex TermMetaBank();

    [GeneratedRegex(@"[^぀-ヿ\[\]\s]+\[([^\]]+)\]")]
    private static partial Regex RubyBase();
}
