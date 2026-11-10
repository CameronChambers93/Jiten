using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Media;

/// <summary>Stores a subtitle deck's timed cues and the local video they belong to, for cutting card media on request.</summary>
public static class VideoLinker
{
    /// <summary>
    /// Adds or replaces the deck's <see cref="DeckSubtitleTrack"/> and <see cref="DeckVideoSource"/> on the context;
    /// the caller saves.
    /// </summary>
    public static async Task StageAsync(JitenDbContext db, int deckId, IEnumerable<SubtitleItem> cues, string videoPath, int audioStream,
                                        int offsetMs, CancellationToken ct = default)
    {
        var cuesJson = DeckSubtitleTrack.FromItems(cues).CuesJson;
        var track = await db.DeckSubtitleTracks.FindAsync([deckId], ct);
        if (track == null)
            db.DeckSubtitleTracks.Add(new DeckSubtitleTrack { DeckId = deckId, CuesJson = cuesJson });
        else
            track.CuesJson = cuesJson;

        var source = await db.DeckVideoSources.FindAsync([deckId], ct);
        if (source == null)
        {
            db.DeckVideoSources.Add(new DeckVideoSource { DeckId = deckId, VideoPath = videoPath, AudioStreamIndex = audioStream, SubtitleOffsetMs = offsetMs });
        }
        else
        {
            source.VideoPath = videoPath;
            source.AudioStreamIndex = audioStream;
            source.SubtitleOffsetMs = offsetMs;
        }
    }
}
