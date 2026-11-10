using System.Text.RegularExpressions;

namespace Jiten.Api.Services.EpisodeImport;

/// <summary>Reading episode numbers and the source a release came from out of file names, and ranking subtitle files by them.</summary>
public static partial class EpisodeSources
{
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".webm", ".mov"];

    /// <summary>
    /// The episode number in a video or subtitle file name: <c>S01E08</c>, <c>第8話</c>, <c> - 08 </c>, <c>[08]</c>,
    /// <c>E08</c>/<c>EP08</c>, in that order. Null when none is found.
    /// </summary>
    public static int? EpisodeNumber(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var pattern in (Regex[])[SeasonEpisode(), Wa(), Dash(), Bracket(), Ep()])
        {
            var m = pattern.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0) return n;
        }

        return null;
    }

    [GeneratedRegex(@"S\d{1,2}[ ._]?E(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"第\s*(\d{1,3})\s*[話回]")]
    private static partial Regex Wa();

    [GeneratedRegex(@"\s-\s(\d{1,3})(?![\dA-Za-z])")]
    private static partial Regex Dash();

    [GeneratedRegex(@"\[(\d{1,3})\]")]
    private static partial Regex Bracket();

    [GeneratedRegex(@"(?<![A-Za-z])EP?[ ._]?(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Ep();

    /// <summary>The streaming service or disc a release names (NF and Netflix are both "netflix"), else null.</summary>
    public static string? ReleaseSource(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        foreach (var (key, tokens) in SourceTokens)
            if (tokens.Any(t => Regex.IsMatch(name, $@"(?<![a-z]){Regex.Escape(t)}(?![a-z])")))
                return key;
        return null;
    }

    private static readonly (string Key, string[] Tokens)[] SourceTokens =
    [
        ("netflix", ["netflix", "nf"]),
        ("amazon", ["amazon", "amzn"]),
        ("crunchyroll", ["crunchyroll", "cr"]),
        ("hidive", ["hidive"]),
        ("disney", ["disney", "dsnp"]),
        ("bluray", ["bluray", "blu-ray", "bdrip", "bd"]),
    ];

    /// <summary>A subtitle file's language from its name: English when it says so, otherwise Japanese (Jimaku's default).</summary>
    public static bool LooksEnglish(string fileName) => EnglishName().IsMatch(fileName);

    [GeneratedRegex(@"(?<![a-z])(en|eng|english)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishName();

    /// <summary>A file holding both languages, which would put English lines among the Japanese sentences.</summary>
    public static bool LooksBilingual(string fileName) => BilingualName().IsMatch(fileName);

    [GeneratedRegex(@"(?<![a-z])((ja|jp|jpn)[-_.+&](en|eng)|(en|eng)[-_.+&](ja|jp|jpn)|dual[-_ .]?sub(s|titles?)?)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex BilingualName();

    [GeneratedRegex(@"(?<![a-z])(ja|jp|jpn|japanese)(?![a-z])|\[cc\]", RegexOptions.IgnoreCase)]
    private static partial Regex JapaneseName();

    private static readonly string[] Archives = [".zip", ".rar", ".7z"];

    /// <summary>
    /// How well a Japanese subtitle file suits a video: .srt over .ass (plain cues), the same release source, and a name
    /// that says Japanese. Null for files that can't be used: archives, bilingual files, and English ones.
    /// </summary>
    public static int? JapaneseScore(string subtitleName, string videoName)
    {
        var ext = Path.GetExtension(subtitleName).ToLowerInvariant();
        if (Archives.Contains(ext) || LooksBilingual(subtitleName) || LooksEnglish(subtitleName)) return null;
        // No .vtt: SubtitleExtractor reads only .srt, .ass and .ssa
        if (ext is not (".srt" or ".ass" or ".ssa")) return null;

        var score = ext == ".srt" ? 3 : 0;
        var source = ReleaseSource(videoName);
        if (source != null && ReleaseSource(subtitleName) == source) score += 2;
        if (JapaneseName().IsMatch(subtitleName)) score += 1;
        return score;
    }

    /// <summary>Embedded English streams: full dialogue first, then SDH, then forced or signs-only.</summary>
    public static int EnglishStreamRank(string? title)
    {
        var t = title?.ToLowerInvariant() ?? "";
        if (t.Contains("forced") || t.Contains("sign") || t.Contains("song")) return 2;
        if (t.Contains("sdh") || t.Contains("cc") || t.Contains("hearing")) return 1;
        return 0;
    }

    public static bool IsJapanese(string? language) => language is "jpn" or "ja" or "jp";

    public static bool IsEnglish(string? language) => language is "eng" or "en";
}
