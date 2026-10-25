using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core.Data;

namespace Jiten.Core.Media;

/// <summary>
/// Reads translation subtitles (ASS/SSA, SRT, WebVTT) into dialogue cues. Fansub ASS files also carry signs, songs and
/// vector drawings as events; those are dropped by style or speaker name, so only spoken lines remain.
/// </summary>
public static partial class EnglishSubtitleParser
{
    public static readonly string[] SupportedExtensions = [".ass", ".ssa", ".srt", ".vtt"];

    /// <summary>Style or speaker names of non-dialogue events (signs, songs, karaoke, notes).</summary>
    [GeneratedRegex(@"(^|[^a-z])(signs?|songs?|kara(oke)?|op|ed|insert|lyrics?|notes?|tl ?notes?|title|credits?|logo)([^a-z]|$)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex NonDialogueName();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverride();

    [GeneratedRegex(@"\\p[1-9]")]
    private static partial Regex AssDrawing();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex Timestamp();

    public static List<SubtitleCue> Parse(string content, string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => ParseAss(content),
            ".srt" or ".vtt" => ParseTimedBlocks(content),
            _ => []
        };

    public static List<SubtitleCue> ParseAss(string content)
    {
        var cues = new List<SubtitleCue>();
        string[]? format = null;
        bool inEvents = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim('﻿');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                format = null;
                continue;
            }

            if (!inEvents) continue;
            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = trimmed[7..].Split(',').Select(f => f.Trim()).ToArray();
                continue;
            }

            if (format == null || !trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            var fields = trimmed[9..].TrimStart().Split(',', format.Length);
            if (fields.Length < format.Length) continue;
            string Field(string name)
            {
                var i = Array.FindIndex(format, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? fields[i].Trim() : "";
            }

            if (NonDialogueName().IsMatch(Field("Style")) || NonDialogueName().IsMatch(Field("Name"))) continue;

            var text = Field("Text");
            if (AssDrawing().IsMatch(text)) continue;

            text = AssOverride().Replace(text, "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ");
            text = Whitespace().Replace(text, " ").Trim();
            if (text.Length == 0) continue;

            var start = ParseTime(Field("Start"));
            var end = ParseTime(Field("End"));
            if (start == null || end == null || end <= start) continue;

            cues.Add(new SubtitleCue(start.Value, end.Value, text));
        }

        return Finish(cues);
    }

    /// <summary>SRT and WebVTT: blocks separated by blank lines, each with a "start --> end" line followed by text.</summary>
    public static List<SubtitleCue> ParseTimedBlocks(string content)
    {
        var cues = new List<SubtitleCue>();
        var blocks = content.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timing < 0) continue;

            var parts = lines[timing].Split("-->");
            var start = ParseTime(parts[0]);
            var end = ParseTime(parts[1]);
            if (start == null || end == null || end <= start) continue;

            var text = string.Join(" ", lines.Skip(timing + 1));
            text = HtmlTag().Replace(AssOverride().Replace(text, ""), "");
            text = Whitespace().Replace(System.Net.WebUtility.HtmlDecode(text), " ").Trim();
            if (text.Length > 0) cues.Add(new SubtitleCue(start.Value, end.Value, text));
        }

        return Finish(cues);
    }

    /// <summary>Sorted by start; exact repeats (the same line split across layers) are kept once.</summary>
    private static List<SubtitleCue> Finish(List<SubtitleCue> cues) =>
        cues.OrderBy(c => c.StartMs).ThenBy(c => c.EndMs).DistinctBy(c => (c.StartMs, c.EndMs, c.Text)).ToList();

    private static int? ParseTime(string text)
    {
        var match = Timestamp().Match(text);
        if (!match.Success) return null;

        var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        // ASS has centiseconds, SRT/VTT milliseconds: scale the fraction by its digit count.
        var fraction = match.Groups[4].Value;
        var ms = int.Parse(fraction, CultureInfo.InvariantCulture) * (int)Math.Pow(10, 3 - fraction.Length);
        return ((hours * 60 + minutes) * 60 + seconds) * 1000 + ms;
    }
}
