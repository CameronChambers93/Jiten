using System.Security.Cryptography;
using System.Text;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>
/// What a user sees of a line where a fix changes it: their own fix, part by part, over the one submitted to the shared line.
/// A shared text fix is already the line's text, so <see cref="Text"/> is only ever the user's.
/// </summary>
/// <param name="Text">The user's own text, with its <paramref name="Tokens"/>.</param>
/// <param name="StartMs">The clip in subtitle time, padding included; set with <paramref name="EndMs"/>.</param>
/// <param name="PictureMs">The screenshot's frame in subtitle time.</param>
/// <param name="Mine">The user has a fix of their own on the line.</param>
/// <param name="Shared">The line has a submitted fix.</param>
public record LineFix(string? Text, byte[]? Tokens, string? English, int? StartMs, int? EndMs, int? PictureMs, bool Mine, bool Shared)
{
    public bool HasRange => StartMs != null && EndMs != null;

    /// <summary>The frame a screenshot shows: the fixed one, else the middle of a fixed clip; null to use the cues'.</summary>
    public int? EffectivePictureMs => PictureMs ?? (HasRange ? (StartMs!.Value + EndMs!.Value) / 2 : null);

    /// <summary>"mine" or "shared", for marking a fixed line.</summary>
    public string Label => Mine ? "mine" : "shared";
}

/// <summary>
/// Anki file names for a line's media. A fixed clip or frame gets a name of its own, from its times, so a re-cut never
/// reuses the name of a different cut (Anki's review window and media sync keep stale copies), and a card whose stored
/// name differs from the expected one is behind.
/// </summary>
public static class SentenceMediaNames
{
    public static string Audio(long sentenceId, LineFix? fix) => $"jiten_{sentenceId}{RangeSuffix(fix)}.mp3";

    public static string Video(long sentenceId, LineFix? fix) => $"jiten_{sentenceId}{RangeSuffix(fix)}.webm";

    public static string Picture(long sentenceId, LineFix? fix) => $"jiten_{sentenceId}{PictureSuffix(fix)}.jpg";

    /// <summary>"" for the cues' clip, else "_" and a key of the fixed range.</summary>
    public static string RangeSuffix(LineFix? fix) => fix is { HasRange: true } ? "_" + Key($"{fix.StartMs}-{fix.EndMs}") : "";

    public static string PictureSuffix(LineFix? fix) => fix?.EffectivePictureMs is { } ms ? "_" + Key($"p{ms}") : "";

    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}

/// <summary>Reads users' fixes and the shared ones, merged into what each line looks like for a user.</summary>
public class SentenceFixStore(JitenDbContext context, UserDbContext userContext)
{
    /// <summary>The fixed lines among <paramref name="sentenceIds"/>; a line with no fix isn't in the result. Without a user, shared fixes only.</summary>
    public async Task<Dictionary<long, LineFix>> ForUserAsync(string? userId, IReadOnlyCollection<long> sentenceIds, CancellationToken ct = default)
    {
        if (sentenceIds.Count == 0) return [];
        var ids = sentenceIds.Distinct().ToList();

        var shared = await context.ExampleSentences.AsNoTracking().IgnoreQueryFilters()
                                  .Where(s => ids.Contains(s.SentenceId) &&
                                              (s.SourceText != null || s.English != null || s.StartMs != null || s.PictureMs != null))
                                  .Select(s => new { s.SentenceId, s.English, s.StartMs, s.EndMs, s.PictureMs })
                                  .ToDictionaryAsync(s => s.SentenceId, ct);

        var mine = userId == null
            ? []
            : await userContext.SentenceFixes.AsNoTracking()
                               .Where(f => f.UserId == userId && ids.Contains(f.ExampleSentenceId))
                               .ToDictionaryAsync(f => f.ExampleSentenceId, ct);

        var result = new Dictionary<long, LineFix>();
        foreach (var id in ids)
        {
            var s = shared.GetValueOrDefault(id);
            var m = mine.GetValueOrDefault(id);
            if (s == null && m == null) continue;

            var mineRange = m?.StartMs != null && m.EndMs != null;
            result[id] = new LineFix(
                m?.Text, m?.Text == null ? null : m.Tokens,
                m?.English ?? s?.English,
                mineRange ? m!.StartMs : s?.StartMs, mineRange ? m!.EndMs : s?.EndMs,
                m?.PictureMs ?? (mineRange ? null : s?.PictureMs),
                m != null, s != null);
        }

        return result;
    }

    public async Task<LineFix?> ForUserAsync(string? userId, long sentenceId, CancellationToken ct = default) =>
        (await ForUserAsync(userId, [sentenceId], ct)).GetValueOrDefault(sentenceId);
}
