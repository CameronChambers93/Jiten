using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.Providers.Jimaku;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.EpisodeImport;

public record MediaFolderEntry(string Name, string Path, bool IsFolder, int? Episode);

/// <param name="Parent">The folder above, or null at a media root.</param>
public record MediaFolderListing(string? Path, string? Parent, List<MediaFolderEntry> Entries, List<string> Roots);

/// <param name="Id">"embedded:&lt;stream&gt;", "jimaku:&lt;url&gt;", "sidecar:&lt;path&gt;"; for English also "none".</param>
public record SubtitleChoice(string Id, string Label, string? Detail);

public record AudioChoice(int Index, string Label);

/// <param name="Conflict">Why this row can't be imported as it stands, e.g. its position is held by an episode with another title.</param>
public record EpisodeScanRow(string VideoPath, string FileName, int? Episode, int? ExistingDeckId, string? ExistingTitle, int ExistingSentences,
                             bool ExistingHasVideo, bool ExistingHasEnglish, int Mined, int OpenFlags, string? Conflict,
                             List<SubtitleChoice> Japanese, string? JapaneseChoice, List<SubtitleChoice> English, string EnglishChoice,
                             List<AudioChoice> Audio, int? AudioStream);

public record EpisodeScanResult(int ParentId, string Title, int? JimakuEntryId, string? JimakuName, string? JimakuError, List<EpisodeScanRow> Rows);

public record EpisodeScanRequest(List<string> Paths, int? JimakuEntryId);

/// <param name="OffsetMs">Given: used as is, with no fit and no retiming. Null: fitted.</param>
/// <param name="ConfirmReplace">Required when the episode exists with mined lines or open flags.</param>
public record EpisodeImportItem(string VideoPath, int Episode, string Japanese, string English, int AudioStream, int? OffsetMs, bool ConfirmReplace);

public record EpisodeImportRequest(List<EpisodeImportItem> Items);

/// <summary>Finds what an episode import would use, for the admin panel's scan; writes nothing.</summary>
public class EpisodeImportService(JitenDbContext context, IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<EpisodeImportService> logger)
{
    /// <summary>The folders and videos in a folder under the media roots, or the roots themselves.</summary>
    public MediaFolderListing ListFolder(string? path)
    {
        var roots = MediaRoots.From(config).ToList();
        if (string.IsNullOrWhiteSpace(path))
            return new MediaFolderListing(null, null, roots.Where(Directory.Exists)
                                                           .Select(r => new MediaFolderEntry(r, r.TrimEnd('/'), true, null)).ToList(), roots);

        var full = Path.GetFullPath(path);
        if (!MediaRoots.Contains(config, full + "/") || !Directory.Exists(full))
            throw new EpisodeImportException("That folder isn't under the media roots.");

        var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                                             .Where(e => !e.Name.StartsWith('.'))
                                             .Where(e => e is DirectoryInfo || EpisodeSources.VideoExtensions.Contains(e.Extension.ToLowerInvariant()))
                                             .Select(e => new MediaFolderEntry(e.Name, e.FullName, e is DirectoryInfo,
                                                                               e is DirectoryInfo ? null : EpisodeSources.EpisodeNumber(e.Name)))
                                             .OrderBy(e => !e.IsFolder).ThenBy(e => e.Episode ?? int.MaxValue).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                                             .ToList();
        var parent = Path.GetDirectoryName(full);
        var atRoot = roots.Any(r => r.TrimEnd('/') == full);
        return new MediaFolderListing(full, atRoot || parent == null ? null : parent, entries, roots);
    }

    /// <summary>The videos named in the request (files, or every video in a folder), with each one's sources and episode.</summary>
    public async Task<EpisodeScanResult> ScanAsync(int parentId, EpisodeScanRequest request, CancellationToken ct)
    {
        var parent = await context.Decks.AsNoTracking().Include(d => d.Links).FirstOrDefaultAsync(d => d.DeckId == parentId && d.ParentDeckId == null, ct)
                     ?? throw new EpisodeImportException("Title not found.");

        var videos = ExpandVideos(request.Paths);
        if (videos.Count == 0) throw new EpisodeImportException("No videos found there.");

        var (entry, jimakuFiles, jimakuError) = await JimakuAsync(parent, request.JimakuEntryId, ct);

        var children = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == parentId)
                                    .Select(d => new { d.DeckId, d.OriginalTitle, d.DeckOrder, d.SentenceCount })
                                    .ToListAsync(ct);
        var childIds = children.Select(c => c.DeckId).ToList();
        var withVideo = await context.DeckVideoSources.Where(v => childIds.Contains(v.DeckId)).Select(v => v.DeckId).ToListAsync(ct);
        var withEnglish = await context.DeckEnglishTracks.Where(v => childIds.Contains(v.DeckId)).Select(v => v.DeckId).ToListAsync(ct);

        var rows = new List<EpisodeScanRow>();
        foreach (var video in videos)
        {
            var name = Path.GetFileName(video);
            var episode = EpisodeSources.EpisodeNumber(name);

            List<VideoMedia.SubtitleStream> subtitleStreams;
            List<VideoMedia.AudioStream> audioStreams;
            try
            {
                subtitleStreams = await VideoMedia.ListSubtitleStreams(video, ct);
                audioStreams = await VideoMedia.ListAudioStreams(video, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ffprobe failed on {Video}", video);
                rows.Add(new EpisodeScanRow(video, name, episode, null, null, 0, false, false, 0, 0, "ffprobe couldn't read this file.",
                                            [], null, [new SubtitleChoice("none", "No English", null)], "none", [], null));
                continue;
            }

            var japanese = JapaneseChoices(video, name, episode, subtitleStreams, jimakuFiles);
            var english = EnglishChoices(video, episode, subtitleStreams, jimakuFiles);
            var audio = audioStreams.Select(a => new AudioChoice(a.Index, $"#{a.Index} {a.Language ?? "und"}{(a.Title is { Length: > 0 } t ? $" · {t}" : "")}"))
                                    .ToList();
            var audioChoice = audioStreams.FirstOrDefault(a => EpisodeSources.IsJapanese(a.Language))?.Index ?? audioStreams.FirstOrDefault()?.Index;

            var title = episode is { } n ? $"Episode {n}" : null;
            var existing = title == null ? null : children.FirstOrDefault(c => c.OriginalTitle == title);
            string? conflict = null;
            if (episode == null)
                conflict = "No episode number in the file name; type one in.";
            else if (existing == null && children.FirstOrDefault(c => c.DeckOrder == episode) is { } holder)
                conflict = $"Position {episode} is held by \"{holder.OriginalTitle}\".";
            else if (japanese.Count == 0)
                conflict = "No Japanese subtitles found: none embedded, on Jimaku or beside the video.";

            var (mined, flags) = existing == null ? (0, 0) : await MinedAndFlagsAsync(existing.DeckId, ct);
            rows.Add(new EpisodeScanRow(video, name, episode, existing?.DeckId, existing?.OriginalTitle, existing?.SentenceCount ?? 0,
                                        existing != null && withVideo.Contains(existing.DeckId), existing != null && withEnglish.Contains(existing.DeckId),
                                        mined, flags, conflict, japanese, japanese.FirstOrDefault()?.Id, english, english[0].Id,
                                        audio, audioChoice));
        }

        // Two videos claiming one episode can't both be imported
        foreach (var dup in rows.Where(r => r.Episode != null).GroupBy(r => r.Episode).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToList())
            rows[rows.IndexOf(dup)] = dup with { Conflict = $"Another video is also episode {dup.Episode}." };

        return new EpisodeScanResult(parentId, parent.OriginalTitle, entry?.Id, entry == null ? null : entry.JapaneseName ?? entry.Name,
                                     jimakuError, rows.OrderBy(r => r.Episode ?? int.MaxValue).ToList());
    }

    /// <summary>Mined lines and open flags on an episode's sentences: the work a replace must keep.</summary>
    public async Task<(int Mined, int Flags)> MinedAndFlagsAsync(int deckId, CancellationToken ct)
    {
        var mined = await context.Database.SqlQuery<int>($@"
            SELECT count(*)::int AS ""Value"" FROM ""user"".""MinedSentences"" m
            JOIN jiten.""ExampleSentences"" s ON s.""SentenceId"" = m.""ExampleSentenceId"" WHERE s.""DeckId"" = {deckId}").SingleAsync(ct);
        var flags = await context.Database.SqlQuery<int>($@"
            SELECT count(*)::int AS ""Value"" FROM ""user"".""SentenceFlags"" f
            JOIN jiten.""ExampleSentences"" s ON s.""SentenceId"" = f.""ExampleSentenceId""
            WHERE s.""DeckId"" = {deckId} AND f.""ResolvedAt"" IS NULL").SingleAsync(ct);
        return (mined, flags);
    }

    private List<string> ExpandVideos(IEnumerable<string> paths)
    {
        var videos = new List<string>();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath))
        {
            var isFolder = Directory.Exists(path);
            // Roots are kept with a trailing slash, so a root folder itself is checked as "/media/"
            if (!MediaRoots.Contains(config, isFolder ? path.TrimEnd('/') + "/" : path))
                throw new EpisodeImportException($"{path} isn't under the media roots.");
            if (isFolder)
                videos.AddRange(Directory.EnumerateFiles(path).Where(f => EpisodeSources.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            else if (File.Exists(path))
                videos.Add(path);
        }

        return videos.Distinct().Order().ToList();
    }

    private async Task<(JimakuEntry? Entry, List<JimakuFile> Files, string? Error)> JimakuAsync(Deck parent, int? entryId, CancellationToken ct)
    {
        var key = config["JimakuApiKey"];
        if (string.IsNullOrWhiteSpace(key) || key == "YourApiKey") return (null, [], "No Jimaku API key in the settings.");

        var http = httpClientFactory.CreateClient();
        try
        {
            JimakuEntry? entry;
            if (entryId is { } id)
            {
                entry = await MetadataProviderHelper.JimakuGetEntryAsync(http, key, id);
            }
            else
            {
                var anilist = parent.Links.Where(l => l.LinkType == LinkType.Anilist).Select(l => AnilistId(l.Url)).FirstOrDefault(i => i != null);
                var tmdb = parent.Links.Where(l => l.LinkType == LinkType.Tmdb).Select(l => l.Url).FirstOrDefault();
                var found = anilist != null
                    ? await MetadataProviderHelper.JimakuSearchAsync(http, key, anilistId: anilist)
                    : tmdb != null
                        ? await MetadataProviderHelper.JimakuSearchAsync(http, key, tmdbId: TmdbId(tmdb), anime: false)
                        : await MetadataProviderHelper.JimakuSearchAsync(http, key, query: parent.OriginalTitle);
                entry = found?.FirstOrDefault();
            }

            if (entry == null) return (null, [], entryId != null ? $"Jimaku has no entry {entryId}." : "No Jimaku entry found for this title; type its id.");
            var files = await MetadataProviderHelper.JimakuGetFilesAsync(http, key, entry.Id) ?? [];
            return (entry, files, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jimaku lookup failed for deck {DeckId}", parent.DeckId);
            return (null, [], "Jimaku didn't answer; try again, or use embedded or sidecar subtitles.");
        }
    }

    private static int? AnilistId(string url) =>
        url.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : (int?)null).FirstOrDefault(n => n != null);

    /// <summary>Jimaku wants "tv:1234"/"movie:1234"; links are TMDB URLs like https://www.themoviedb.org/tv/1234.</summary>
    private static string? TmdbId(string url)
    {
        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(parts, p => p is "tv" or "movie");
        return i >= 0 && i + 1 < parts.Length ? $"{parts[i]}:{parts[i + 1].Split('-')[0]}" : null;
    }

    private static List<SubtitleChoice> JapaneseChoices(string video, string name, int? episode, List<VideoMedia.SubtitleStream> streams,
                                                        List<JimakuFile> jimaku)
    {
        var choices = new List<SubtitleChoice>();
        choices.AddRange(streams.Where(s => s.IsText && EpisodeSources.IsJapanese(s.Language))
                                .Select(s => new SubtitleChoice($"embedded:{s.Index}", $"Embedded #{s.Index}: {s.Title ?? "Japanese"}", s.Codec)));

        if (episode != null)
            choices.AddRange(jimaku.Where(f => EpisodeSources.EpisodeNumber(f.Name) == episode)
                                   .Select(f => (File: f, Score: EpisodeSources.JapaneseScore(f.Name, name)))
                                   .Where(f => f.Score != null)
                                   .OrderByDescending(f => f.Score).ThenBy(f => f.File.Name)
                                   .Select(f => new SubtitleChoice($"jimaku:{f.File.Url}", $"Jimaku: {f.File.Name}",
                                                                   EpisodeSources.ReleaseSource(f.File.Name) is { } s ? s : null)));

        choices.AddRange(Sidecars(video).Where(s => Path.GetExtension(s).ToLowerInvariant() != ".vtt")
                                        .Where(s => !EpisodeSources.LooksEnglish(SidecarSuffix(video, s)) && !EpisodeSources.LooksBilingual(SidecarSuffix(video, s)))
                                        .Select(s => new SubtitleChoice($"sidecar:{s}", $"Beside the video: {Path.GetFileName(s)}", null)));
        return choices;
    }

    private static List<SubtitleChoice> EnglishChoices(string video, int? episode, List<VideoMedia.SubtitleStream> streams, List<JimakuFile> jimaku)
    {
        var choices = new List<SubtitleChoice>();
        choices.AddRange(streams.Where(s => s.IsText && EpisodeSources.IsEnglish(s.Language))
                                .OrderBy(s => EpisodeSources.EnglishStreamRank(s.Title)).ThenBy(s => s.Index)
                                .Select(s => new SubtitleChoice($"embedded:{s.Index}", $"Embedded #{s.Index}: {s.Title ?? "English"}", s.Codec)));
        choices.AddRange(Sidecars(video).Where(s => EpisodeSources.LooksEnglish(SidecarSuffix(video, s)) && !EpisodeSources.LooksBilingual(SidecarSuffix(video, s)))
                                        .Select(s => new SubtitleChoice($"sidecar:{s}", $"Beside the video: {Path.GetFileName(s)}", null)));
        if (episode != null)
            choices.AddRange(jimaku.Where(f => EpisodeSources.EpisodeNumber(f.Name) == episode && EpisodeSources.LooksEnglish(f.Name)
                                               && !EpisodeSources.LooksBilingual(f.Name)
                                               && Path.GetExtension(f.Name).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt")
                                   .Select(f => new SubtitleChoice($"jimaku:{f.Url}", $"Jimaku: {f.Name}", null)));
        choices.Add(new SubtitleChoice("none", "No English", null));
        return choices;
    }

    /// <summary>What a sidecar adds to the video's name (".ja.srt"): its language is there, not in the release name.</summary>
    private static string SidecarSuffix(string video, string sidecar) =>
        Path.GetFileName(sidecar)[Path.GetFileNameWithoutExtension(video).Length..];

    /// <summary>Subtitle files next to the video that start with its name (Show.S01E08.ja.srt beside Show.S01E08.mkv).</summary>
    private static IEnumerable<string> Sidecars(string video)
    {
        var dir = Path.GetDirectoryName(video)!;
        var stem = Path.GetFileNameWithoutExtension(video);
        return Directory.EnumerateFiles(dir)
                        .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                                    Path.GetExtension(f).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt")
                        .Order();
    }
}

/// <summary>A problem the admin can fix (a path outside the roots, a missing title), shown as is.</summary>
public class EpisodeImportException(string message) : Exception(message);
