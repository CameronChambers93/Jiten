namespace Jiten.Api.Services.Translation.Subtitles;

/// <summary>An episode of the title being searched: its deck and episode number (1-based, in deck order).</summary>
public record EpisodeRef(int DeckId, int Episode);

public record SubtitleSearch(string Query, int? Season, IReadOnlyList<EpisodeRef> Episodes);

/// <param name="Id">Opaque to callers; passed back to <see cref="ISubtitleSource.DownloadAsync"/>.</param>
/// <param name="Note">Extra detail shown beside the release, e.g. the stream title or "hearing impaired".</param>
public record SubtitleCandidate(string Id, int Episode, string Release, int? Downloads, string? Note);

public record SubtitleFile(string FileName, string Content);

/// <summary>
/// Somewhere English subtitles can be found for a title's episodes. To add one, implement this and register it in
/// Program.cs; the admin page draws its settings form from <see cref="Fields"/>.
/// </summary>
public interface ISubtitleSource
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<SettingField> Fields { get; }

    /// <summary>Candidates for the searched episodes; ones that can't be tied to a single episode are left out.</summary>
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearch search, ProviderConfig config, CancellationToken ct);

    Task<SubtitleFile> DownloadAsync(string candidateId, ProviderConfig config, CancellationToken ct);
}

/// <summary>A source failed (bad key, quota, network); the message is shown to the admin.</summary>
public class SubtitleSourceException(string message, Exception? inner = null) : Exception(message, inner);
