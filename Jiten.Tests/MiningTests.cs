using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Jiten.Api.Services.Mining;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Frequency;

namespace Jiten.Tests;

public class MiningTests
{
    private static MemoryStream Zip(string index, params string[] banks)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }

            Add("index.json", index);
            for (int i = 0; i < banks.Length; i++) Add($"term_meta_bank_{i + 1}.json", banks[i]);
        }

        stream.Position = 0;
        return stream;
    }

    // Shapes from JPDB v2.2 Kana: a bare kana term, a term with its reading, and the ㋕ rank for writing it in kana.
    private const string JpdbBank = """
        [["こと","freq",{"value":15,"displayValue":"15㋕"}],
         ["思う","freq",{"reading":"おもう","frequency":{"value":20,"displayValue":"20"}}],
         ["思う","freq",{"reading":"おもう","frequency":{"value":9493,"displayValue":"9493㋕"}}],
         ["決まる","freq",{"reading":"きまる","frequency":{"value":900,"displayValue":"900"}}],
         ["極まる","freq",{"reading":"きわまる","frequency":{"value":30000,"displayValue":"30000"}}],
         ["事","freq",{"reading":"こと","frequency":{"value":120,"displayValue":"120"}}],
         ["犯罪者","freq",7050],
         ["x","pitch",{}]]
        """;

    [Fact]
    public void Read_ParsesEveryEntryShape()
    {
        var file = YomitanFrequencyDictionary.Read(Zip("""{"title":"JPDBv2㋕","revision":"JPDB v2.2","frequencyMode":"rank-based"}""", JpdbBank));

        file.Title.Should().Be("JPDBv2㋕");
        file.Revision.Should().Be("JPDB v2.2");
        file.Entries.Should().HaveCount(7);
        file.Entries.Should().Contain(new YomitanFrequencyEntry("思う", "おもう", 9493)).And.Contain(new YomitanFrequencyEntry("犯罪者", null, 7050));
    }

    [Fact]
    public void Read_TurnsOccurrenceCountsIntoRanks()
    {
        var file = YomitanFrequencyDictionary.Read(Zip("""{"title":"counts","frequencyMode":"occurrence-based"}""", """[["a","freq",10],["b","freq",500],["c","freq",42]]"""));

        file.Entries.Select(e => (e.Term, e.Value)).Should().Equal(("a", 3), ("b", 1), ("c", 2));
    }

    [Fact]
    public void Rank_TakesTheLowestMatchingEntryForEachForm()
    {
        var entries = YomitanFrequencyDictionary.Read(Zip("""{"title":"t"}""", JpdbBank)).Entries;
        FrequencyForm[] forms =
        [
            new(1, 0, "思う", "思[おも]う", JmDictFormType.KanjiForm),
            new(1, 1, "おもう", "おもう", JmDictFormType.KanaForm),
            new(2, 0, "事", "事[こと]", JmDictFormType.KanjiForm),
            new(2, 2, "こと", "こと", JmDictFormType.KanaForm),
            new(3, 0, "決まる", "決[き]まる", JmDictFormType.KanjiForm),
            new(3, 2, "極まる", "極[きま]る", JmDictFormType.KanjiForm), // 極まる read きわまる is another word's entry
            new(4, 0, "犯罪者", "犯罪者[はんざいしゃ]", JmDictFormType.KanjiForm),
            new(5, 0, "未知", "未知[みち]", JmDictFormType.KanjiForm)
        ];

        var ranks = YomitanFrequencyDictionary.Rank(entries, forms);

        ranks.Should().BeEquivalentTo(new Dictionary<(int, short), int>
        {
            [(1, 0)] = 20, [(1, 1)] = 20, [(2, 0)] = 120, [(2, 2)] = 15, [(3, 0)] = 900, [(4, 0)] = 7050
        });
    }

    [Theory]
    [InlineData("決[き]まる", "きまる")]
    [InlineData(" 問題[もんだい]児[じ]", "もんだいじ")]
    [InlineData("Ｔ[ティー]シャツ", "ティーシャツ")]
    public void RubyReading_ReadsTheKana(string ruby, string reading) => YomitanFrequencyDictionary.RubyReading(ruby).Should().Be(reading);

    [Theory]
    [InlineData("verb", "v5r", "vi", "uk")]
    [InlineData("verb", "v1", "vt")]
    [InlineData("noun", "n", "vs", "vt")]
    [InlineData("i-adjective", "adj-i")]
    [InlineData("na-adjective", "adj-na", "n")]
    [InlineData("noun", "n", "uk", "n-suf")]
    [InlineData("adverb", "adv", "adv-to")]
    [InlineData("expression", "exp")]
    [InlineData("other", "int")]
    public void ClassifyWordType_PicksTheMainType(string expected, params string[] tags) => MiningSentenceService.ClassifyWordType(tags).Should().Be(expected);
}
