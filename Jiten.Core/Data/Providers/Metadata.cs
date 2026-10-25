namespace Jiten.Core.Data.Providers;

public class Metadata
{
    public string? FilePath { get; set; }
    public string? OriginalFileName { get; set; }
    public string OriginalTitle { get; set; } = "Unknown";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string? Description { get; set; }
    public string? Image { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public List<Link> Links { get; set; } = new();
    public List<Metadata> Children { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public int? Rating { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<MetadataTag> Tags { get; set; } = new();
    public bool IsAdultOnly { get; set; }
    public bool IsNotOriginallyJapanese { get; set; }
    public List<MetadataRelation> Relations { get; set; } = new();
    public long? SpeechDuration { get; set; }
    public long? SpeechMoraCount { get; set; }
    public List<DeckDictionaryEntry> DictionaryEntries { get; set; } = new();

    /// <summary>The episode's video under the media root, for cutting card media; read by --link-video.</summary>
    public string? VideoPath { get; set; }

    /// <summary>ffmpeg stream index of the Japanese audio; the first jpn-tagged (else first) audio stream when null.</summary>
    public int? AudioStream { get; set; }

    /// <summary>Shift applied to the subtitle times to match the video; detected from the audio when null.</summary>
    public int? SubtitleOffsetMs { get; set; }
}
