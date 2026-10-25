namespace Jiten.Core.Data;

/// <summary>
/// The local video a subtitle deck was made from, so card media (sentence audio, screenshot) can be cut from it on demand.
/// The deck's <see cref="DeckSubtitleTrack"/> holds the cue times; <see cref="SubtitleOffsetMs"/> maps them onto the video.
/// </summary>
public class DeckVideoSource
{
    public int DeckId { get; set; }

    /// <summary>Absolute path inside the media root (read-only library mount).</summary>
    public string VideoPath { get; set; } = "";

    /// <summary>ffmpeg stream index of the Japanese audio track.</summary>
    public int AudioStreamIndex { get; set; }

    /// <summary>Added to every cue time to get the video time; subtitles from another release are often shifted.</summary>
    public int SubtitleOffsetMs { get; set; }

    public Deck Deck { get; set; } = null!;
}
