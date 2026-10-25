using Jiten.Core;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Translation.Subtitles;

/// <summary>English text subtitle tracks inside the episodes' own linked videos; no network or key needed.</summary>
public class EmbeddedSubtitleSource(JitenDbContext context, IConfiguration configuration) : ISubtitleSource
{
    public string Id => "embedded";
    public string Name => "Tracks in the linked videos";
    public IReadOnlyList<SettingField> Fields => [];

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearch search, ProviderConfig config, CancellationToken ct)
    {
        var deckIds = search.Episodes.Select(e => e.DeckId).ToList();
        var videos = await context.DeckVideoSources.AsNoTracking().Where(v => deckIds.Contains(v.DeckId))
                                  .ToDictionaryAsync(v => v.DeckId, v => v.VideoPath, ct);

        var candidates = new List<SubtitleCandidate>();
        foreach (var episode in search.Episodes)
        {
            if (!videos.TryGetValue(episode.DeckId, out var path) || !IsUnderMediaRoot(path) || !File.Exists(path)) continue;

            foreach (var stream in await VideoMedia.ListSubtitleStreams(path, ct))
            {
                // Image subtitles (PGS, VobSub) would need OCR, which isn't supported yet.
                if (!IsEnglish(stream) || !stream.IsText) continue;
                candidates.Add(new SubtitleCandidate($"{episode.DeckId}:{stream.Index}", episode.Episode,
                                                     $"{Path.GetFileName(path)} #{stream.Index}", null, stream.Title));
            }
        }

        return candidates;
    }

    public async Task<SubtitleFile> DownloadAsync(string candidateId, ProviderConfig config, CancellationToken ct)
    {
        var parts = candidateId.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var deckId) || !int.TryParse(parts[1], out var index))
            throw new SubtitleSourceException("Bad embedded subtitle id.");

        var path = await context.DeckVideoSources.AsNoTracking().Where(v => v.DeckId == deckId).Select(v => v.VideoPath).FirstOrDefaultAsync(ct);
        if (path == null || !IsUnderMediaRoot(path)) throw new SubtitleSourceException("The episode has no linked video.");

        var stream = (await VideoMedia.ListSubtitleStreams(path, ct)).FirstOrDefault(s => s.Index == index && s.IsText)
                     ?? throw new SubtitleSourceException($"No text subtitle stream #{index} in {Path.GetFileName(path)}.");
        var (extension, content) = await VideoMedia.ExtractSubtitle(path, stream, ct);
        return new SubtitleFile(Path.GetFileNameWithoutExtension(path) + extension, content);
    }

    /// <summary>Tagged English, or untagged with an English-looking title; signs-only tracks are left out.</summary>
    private static bool IsEnglish(VideoMedia.SubtitleStream stream)
    {
        var title = stream.Title ?? "";
        if (title.Contains("sign", StringComparison.OrdinalIgnoreCase) || title.Contains("song", StringComparison.OrdinalIgnoreCase)) return false;
        return stream.Language is "eng" or "en" || (stream.Language is null or "und" && title.Contains("eng", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsUnderMediaRoot(string path)
    {
        var root = Path.GetFullPath(configuration["MediaRoot"] ?? "/media").TrimEnd('/') + "/";
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }
}
