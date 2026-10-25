namespace Jiten.Core.Data;

/// <summary>
/// English subtitles for a subtitle deck, from a fansub or official release, so example sentences can show the English
/// spoken over the same lines. Cues stay in their own release's timing; <see cref="OffsetMs"/> maps them onto the deck's
/// Japanese <see cref="DeckSubtitleTrack"/>.
/// </summary>
public class DeckEnglishTrack
{
    public int DeckId { get; set; }

    /// <summary>Cues in the same JSON shape as <see cref="DeckSubtitleTrack.CuesJson"/>.</summary>
    public string CuesJson { get; set; } = "[]";

    /// <summary>Added to every English cue time to line it up with the Japanese cues.</summary>
    public int OffsetMs { get; set; }

    /// <summary>Where the subtitles came from, e.g. "SubDL: [HorribleSubs] Osomatsu-san S2 - 01 [720p]".</summary>
    public string Source { get; set; } = "";

    public DateTime SavedAt { get; set; }

    public List<SubtitleCue> GetCues() => new DeckSubtitleTrack { CuesJson = CuesJson }.GetCues();
}
