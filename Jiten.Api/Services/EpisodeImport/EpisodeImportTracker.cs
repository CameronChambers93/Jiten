using System.Collections.Concurrent;

namespace Jiten.Api.Services.EpisodeImport;

public enum EpisodeImportStep { Queued, Subtitles, Timing, Parsing, Video, English, Done, Failed }

public record TimingSegmentDto(int FromMs, int OffsetMs);

/// <param name="Matched">Sentences that match a cue, so they get a clip.</param>
/// <param name="VideoFit">How the video offset was found: "given", "audio", "English track".</param>
/// <param name="AudioOffsetMs">The audio's own fit of the first segment, when the English track decided, as a check.</param>
/// <param name="Segments">More than one when the cues were retimed.</param>
public record EpisodeImportReport(int DeckId, int Sentences, int Matched, int WithEnglish, List<string> Unmatched,
                                  string JapaneseSource, string? EnglishSource, int VideoOffsetMs, string VideoFit, int? AudioOffsetMs,
                                  int? EnglishOffsetMs, List<TimingSegmentDto> Segments, List<string> Warnings, int KeptMined, int KeptFlags);

public class EpisodeImportProgress
{
    public required int Episode { get; init; }
    public required string FileName { get; init; }
    public EpisodeImportStep Step { get; set; } = EpisodeImportStep.Queued;
    public string? Error { get; set; }
    public EpisodeImportReport? Report { get; set; }
}

public class EpisodeImportRun
{
    public required string Id { get; init; }
    public required int ParentId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public required List<EpisodeImportProgress> Episodes { get; init; }
    public bool Finished { get; set; }

    /// <summary>What happened to the title afterwards (totals rebuilt), or why not.</summary>
    public string? TitleNote { get; set; }
}

/// <summary>
/// Progress of episode imports, kept in memory for the panel to poll. The job runs in the API's own Hangfire server, so
/// it shares this; a restart loses the progress (the imported episodes stay).
/// </summary>
public class EpisodeImportTracker
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, EpisodeImportRun> _runs = new();

    public EpisodeImportRun Start(int parentId, IEnumerable<(int Episode, string FileName)> episodes)
    {
        foreach (var old in _runs.Values.Where(r => r.StartedAt < DateTime.UtcNow - Keep).ToList())
            _runs.TryRemove(old.Id, out _);

        var run = new EpisodeImportRun
        {
            Id = Guid.NewGuid().ToString("N"), ParentId = parentId,
            Episodes = episodes.Select(e => new EpisodeImportProgress { Episode = e.Episode, FileName = e.FileName }).ToList()
        };
        _runs[run.Id] = run;
        return run;
    }

    public EpisodeImportRun? Get(string id) => _runs.GetValueOrDefault(id);

    /// <summary>The latest run for a title, so the panel shows it again after a reload.</summary>
    public EpisodeImportRun? Latest(int parentId) => _runs.Values.Where(r => r.ParentId == parentId).MaxBy(r => r.StartedAt);
}
