using FluentAssertions;
using Jiten.Api.Services.EpisodeImport;

namespace Jiten.Tests;

public class EpisodeSourcesTests
{
    [Theory]
    [InlineData("DEATH NOTE S01E08 Glare 1080p NF WEB-DL DDP2 0 H 264-Kitsune.mkv", 8)]
    [InlineData("DEATH.NOTE.-デスノート-.S01E08.STORY.08.「目線」.WEBRip.Netflix.ja[cc].srt", 8)]
    [InlineData("DEATH NOTE - 08 [Re-Timed for Netflix(US)].srt", 8)]
    [InlineData("[SubsPlease] Odd Taxi - 13 (1080p) [ABCD1234].mkv", 13)]
    [InlineData("[Group] Show [05][1080p].mkv", 5)]
    [InlineData("ゆるキャン△ 第3話.ass", 3)]
    [InlineData("Show.EP12.1080p.mkv", 12)]
    [InlineData("Show.E07.mkv", 7)]
    [InlineData("Movie (2016) 1080p H 264.mkv", null)]
    [InlineData("[SubsPlease] Show - 2nd Season - 05 (1080p).mkv", 5)]
    [InlineData("Show - 3rd - 12.srt", 12)]
    public void Episode_numbers_come_from_the_file_name(string name, int? episode) =>
        EpisodeSources.EpisodeNumber(name).Should().Be(episode);

    [Fact]
    public void For_a_Netflix_rip_the_Netflix_ja_cc_file_beats_the_other_Netflix_retime()
    {
        const string video = "DEATH NOTE S01E08 Glare 1080p NF WEB-DL DDP2 0 H 264-Kitsune.mkv";
        var cc = EpisodeSources.JapaneseScore("DEATH.NOTE.-デスノート-.S01E08.STORY.08.「目線」.WEBRip.Netflix.ja[cc].srt", video);
        var retimed = EpisodeSources.JapaneseScore("DEATH NOTE - 08 [Re-Timed for Netflix(US)].srt", video);
        var ass = EpisodeSources.JapaneseScore("DEATH NOTE - 08.ass", video);

        cc.Should().BeGreaterThan(retimed!.Value);
        retimed.Should().BeGreaterThan(ass!.Value);
    }

    [Theory]
    [InlineData("Show - 01.ja-en.ass")]
    [InlineData("Show - 01.en.srt")]
    [InlineData("Show - 01 English.srt")]
    [InlineData("Show S01.zip")]
    public void Bilingual_English_and_archive_files_are_not_Japanese_choices(string name) =>
        EpisodeSources.JapaneseScore(name, "Show - 01.mkv").Should().BeNull();

    [Theory]
    [InlineData("Show - 01.ja.vtt")]
    [InlineData("Show - 01 [Dual-Subs].ass")]
    public void Unreadable_and_dual_subtitle_files_are_not_Japanese_choices(string name) =>
        EpisodeSources.JapaneseScore(name, "Show - 01.mkv").Should().BeNull();

    [Theory]
    [InlineData("[Group] Show - 05 [Dual-Audio][1080p].ja.srt", false)]
    [InlineData("Ninja.English.srt", false)]
    [InlineData("Show.ja-en.ass", true)]
    [InlineData("Show [Dual Sub].ass", true)]
    public void Only_files_holding_both_languages_count_as_bilingual(string name, bool bilingual) =>
        EpisodeSources.LooksBilingual(name).Should().Be(bilingual);

    [Fact]
    public void Full_English_ranks_before_SDH_and_signs()
    {
        EpisodeSources.EnglishStreamRank("English").Should().Be(0);
        EpisodeSources.EnglishStreamRank(null).Should().Be(0);
        EpisodeSources.EnglishStreamRank("English (SDH)").Should().Be(1);
        EpisodeSources.EnglishStreamRank("English [Forced]").Should().Be(2);
        EpisodeSources.EnglishStreamRank("Signs & Songs").Should().Be(2);
    }
}
