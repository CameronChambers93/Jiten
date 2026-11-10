using System.Text;
using Hangfire;
using Jiten.Api.Services;
using Jiten.Api.Services.EpisodeImport;
using Jiten.Api.Services.Translation;
using Jiten.Api.Services.Translation.Subtitles;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Jobs;

/// <summary>
/// Imports episodes of an existing title from local video, one at a time: the Japanese subtitles, timing fitted in
/// windows (retiming the cues where the offset changes), sentences parsed from those cues and stored as the episode
/// alone, the video linked, the English saved; then the title's totals rebuilt and the caches cleared.
/// See the vault's "Episode import design".
/// </summary>
public class EpisodeImportJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    EpisodeImportTracker tracker,
    EpisodeImportService scans,
    EnglishSubtitleService english,
    IMemoryCache cache,
    SentenceStatsCache statsCache,
    IBackgroundJobClient backgroundJobs,
    IPendingCoverageQueue pendingCoverageQueue,
    ILogger<EpisodeImportJob> logger)
{
    /// <summary>Where each episode's subtitle files are kept, so it can be re-imported without Jimaku.</summary>
    private string FilesRoot => config["EpisodeImportPath"] ?? "/data/episode-import";

    /// <summary>English this far from minus the video offset is reported: the two fits disagree.</summary>
    private const int AgreeMs = 500;

    [Queue("parse")]
    [AutomaticRetry(Attempts = 0)]
    public async Task Run(string runId, int parentId, List<EpisodeImportItem> items)
    {
        var run = tracker.Get(runId) ?? tracker.Start(parentId, items.Select(i => (i.Episode, Path.GetFileName(i.VideoPath))));
        var imported = new List<int>();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var progress = run.Episodes[i];
                try
                {
                    var deckId = await ImportOneAsync(parentId, items[i], progress, CancellationToken.None);
                    imported.Add(deckId);
                    progress.Step = EpisodeImportStep.Done;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Episode import failed: deck {ParentId} episode {Episode}", parentId, items[i].Episode);
                    progress.Step = EpisodeImportStep.Failed;
                    progress.Error = ex is EpisodeImportException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            if (imported.Count > 0)
            {
                await JitenHelper.RebuildParentFromChildren(contextFactory, parentId);
                statsCache.Clear();
                await pendingCoverageQueue.AddManyAsync(imported.Append(parentId).ToList());
                foreach (var deckId in imported)
                    backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(deckId));
                backgroundJobs.Enqueue<StatsComputationJob>(job => job.ComputeDeckCoverageStats(parentId));
                backgroundJobs.Enqueue<DifficultyComputationJob>(job => job.ComputeDeckDifficulty(parentId, true));

                await using var db = await contextFactory.CreateDbContextAsync();
                var count = await db.Decks.CountAsync(d => d.ParentDeckId == parentId);
                run.TitleNote = $"The title's totals and word list were rebuilt from its {count} episodes. Difficulty and coverage are recomputing in the background.";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Episode import: rebuilding deck {ParentId} failed", parentId);
            run.TitleNote = $"The episodes were imported, but rebuilding the title's totals failed: {ex.Message}";
        }
        finally
        {
            run.Finished = true;
        }
    }

    private async Task<int> ImportOneAsync(int parentId, EpisodeImportItem item, EpisodeImportProgress progress, CancellationToken ct)
    {
        if (!MediaRoots.Contains(config, item.VideoPath) || !File.Exists(item.VideoPath))
            throw new EpisodeImportException("The video isn't under the media roots any more.");
        if ((await VideoMedia.ListAudioStreams(item.VideoPath, ct)).All(a => a.Index != item.AudioStream))
            throw new EpisodeImportException($"The video has no audio stream #{item.AudioStream}.");

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var parent = await db.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == parentId && d.ParentDeckId == null, ct)
                     ?? throw new EpisodeImportException("Title not found.");
        var title = $"Episode {item.Episode}";
        var existing = await db.Decks.AsNoTracking().Where(d => d.ParentDeckId == parentId)
                               .Select(d => new { d.DeckId, d.OriginalTitle, d.DeckOrder }).ToListAsync(ct);
        var current = existing.FirstOrDefault(d => d.OriginalTitle == title);
        if (current == null && existing.FirstOrDefault(d => d.DeckOrder == item.Episode) is { } holder)
            throw new EpisodeImportException($"Position {item.Episode} is held by \"{holder.OriginalTitle}\".");
        if (current != null && !item.ConfirmReplace)
        {
            var (mined, flags) = await scans.MinedAndFlagsAsync(current.DeckId, ct);
            if (mined + flags > 0) throw new EpisodeImportException($"Episode {item.Episode} has {mined} mined lines and {flags} open flags; tick Replace to go ahead.");
        }

        var dir = Path.Combine(FilesRoot, parentId.ToString(), item.Episode.ToString("00"));
        Directory.CreateDirectory(dir);
        var warnings = new List<string>();

        // 1. Subtitles
        progress.Step = EpisodeImportStep.Subtitles;
        var (jaPath, jaSource) = await FetchAsync(item.Japanese, item.VideoPath, dir, "ja", ct);
        var extractor = new SubtitleExtractor();
        var items = await extractor.ExtractItems(jaPath);
        if (items.Count == 0) throw new EpisodeImportException($"No dialogue in the Japanese subtitles ({jaSource}).");

        List<SubtitleCue>? englishCues = null;
        string? englishSource = null;
        var englishOnVideoClock = item.English.StartsWith("embedded:");
        if (item.English != "none")
        {
            var (enPath, enSource) = await FetchAsync(item.English, item.VideoPath, dir, "en", ct);
            englishCues = EnglishSubtitleParser.Parse(await File.ReadAllTextAsync(enPath, ct), Path.GetExtension(enPath));
            englishSource = enSource;
            if (englishCues.Count == 0)
            {
                warnings.Add($"The English ({enSource}) has no dialogue lines; none saved.");
                englishCues = null;
            }
        }

        // 2. Timing
        progress.Step = EpisodeImportStep.Timing;
        TimingFit fit;
        string how;
        int? audioOffset = null;
        if (item.OffsetMs is { } given)
        {
            var sorted = items.OrderBy(c => c.StartMs).ToList();
            fit = new TimingFit(given, given, 0, 0, [new TimingSegment(0, sorted[0].StartMs, given)], []);
            how = "given";
        }
        else
        {
            var pcm = await VideoMedia.DecodePcm8k(item.VideoPath, item.AudioStream, ct);
            var audioFit = SubtitleTimingFitter.Fit(SubtitleOffsetDetector.ActivityTrack.FromSpeech(pcm), items);
            fit = audioFit;
            how = "audio";

            // Embedded English is on the video's clock, so it's a second, sharper reference; it wins when both exist
            if (englishCues != null && englishOnVideoClock)
            {
                var englishFit = SubtitleTimingFitter.Fit(SubtitleOffsetDetector.ActivityTrack.FromCues(englishCues.Select(c => (c.StartMs, c.EndMs))), items,
                                                            ignoreSoundOnly: true);
                fit = englishFit;
                how = "English track";
                audioOffset = audioFit.OffsetMs;
                if (Math.Abs(audioFit.OffsetMs - englishFit.OffsetMs) > AgreeMs || audioFit.Segments.Count != englishFit.Segments.Count)
                    warnings.Add($"The audio fits differently ({Describe(audioFit)}) from the English track ({Describe(englishFit)}); the English track's timing was used.");
            }

            if (fit.Score <= fit.ScoreAtZero && fit.OffsetMs != 0)
                warnings.Add("The timing fit is weak: no shift lines the cues up better than none. Check a few clips.");
        }

        var cues = fit.Apply(items);
        if (fit.Retimed)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "ja.retimed.srt"), ToSrt(cues), ct);
            foreach (var (seg, next) in fit.Segments.Zip(fit.Segments.Skip(1)))
            {
                warnings.Add($"The timing changes at {Clock(next.StartMs + seg.OffsetMs)}: {next.OffsetMs - seg.OffsetMs:+#,0;-#,0} ms. " +
                             "The Japanese cues after it were moved to match the video.");
                // Against audio the offsets are reliable but where the change falls isn't (Death Note E11: 2.5 min late)
                if (how == "audio")
                    warnings.Add($"That change was placed from the audio alone, which can put it in the wrong silence: it could be anywhere " +
                                 $"from {Clock(next.SearchFromMs + seg.OffsetMs)} to {Clock(next.SearchToMs + seg.OffsetMs)}. Check clips there, " +
                                 "or import with embedded English, which places it reliably.");
            }
        }

        // 3. Sentences, from the same cues, as this one episode
        progress.Step = EpisodeImportStep.Parsing;
        var text = Parser.Parser.SubtitleLinesEnabled ? await extractor.ExtractWithCueBreaks(jaPath) : await extractor.Extract(jaPath);
        // Raw text as the CLI and ParseJob keep it: the difficulty job needs it
        var child = await Parser.Parser.ParseTextToDeck(contextFactory, text, config.GetValue<bool>("StoreRawText"), true, parent.MediaType);
        var stats = await Parser.SubtitleMoraRateCalculator.ComputeAsync(items);
        child.OriginalTitle = title;
        child.DeckOrder = current?.DeckOrder ?? item.Episode;
        child.MediaType = parent.MediaType;
        child.DifficultyOverride = -1;
        child.SpeechDuration = stats.DurationMs;
        child.SpeechMoraCount = stats.MoraCount;
        child.OriginalFileName = Path.GetFileName(jaPath);

        var shell = new Deck
        {
            DeckId = parentId, OriginalTitle = parent.OriginalTitle, MediaType = parent.MediaType, LastUpdate = DateTimeOffset.UtcNow,
            Children = [child]
        };
        child.ParentDeck = shell;
        await JitenHelper.InsertDeck(contextFactory, shell, [], update: true, childrenOnly: true);

        var deckId = await db.Decks.Where(d => d.ParentDeckId == parentId && d.OriginalTitle == title).Select(d => d.DeckId).FirstAsync(ct);

        // 4. Video
        progress.Step = EpisodeImportStep.Video;
        await VideoLinker.StageAsync(db, deckId, cues, item.VideoPath, item.AudioStream, fit.OffsetMs, ct);
        await db.SaveChangesAsync(ct);
        SentenceCueLookup.Invalidate(cache, deckId);

        // 5. English
        progress.Step = EpisodeImportStep.English;
        var japaneseCues = cues.Select(c => new SubtitleCue(c.StartMs, c.EndMs, c.Text.Trim())).Where(c => c.Text.Length > 0).ToList();
        int? englishOffset = null;
        if (englishCues != null)
        {
            var detected = SubtitleOffsetDetector.DetectAgainstCues(japaneseCues.Select(c => (c.StartMs, c.EndMs)), englishCues.Select(c => (c.StartMs, c.EndMs)));
            englishOffset = detected.OffsetMs;
            if (englishOnVideoClock && Math.Abs(detected.OffsetMs + fit.OffsetMs) > AgreeMs)
                warnings.Add($"The English offset ({detected.OffsetMs:+#,0;-#,0} ms) doesn't mirror the video offset ({fit.OffsetMs:+#,0;-#,0} ms).");
            await english.SaveTrackAsync(deckId, englishCues, detected.OffsetMs, englishSource!, ct);
        }
        else if (await db.DeckEnglishTracks.AsNoTracking().FirstOrDefaultAsync(t => t.DeckId == deckId, ct) is { } kept)
        {
            // The episode's English stays, but its offset was fitted to the old Japanese cues: fit it to the new ones
            var keptCues = kept.GetCues();
            var detected = SubtitleOffsetDetector.DetectAgainstCues(japaneseCues.Select(c => (c.StartMs, c.EndMs)), keptCues.Select(c => (c.StartMs, c.EndMs)));
            englishCues = keptCues;
            englishOffset = detected.OffsetMs;
            englishSource = kept.Source;
            if (detected.OffsetMs != kept.OffsetMs)
                warnings.Add($"Kept the episode's English ({kept.Source}) and refitted its offset to the new cues: {kept.OffsetMs:+#,0;-#,0} → {detected.OffsetMs:+#,0;-#,0} ms.");
            await english.SaveTrackAsync(deckId, keptCues, detected.OffsetMs, kept.Source, ct);
        }
        else
        {
            SentenceTranslationService.InvalidateEnglish(cache, deckId);
        }

        // 6. Report
        var sentences = await db.ExampleSentences.AsNoTracking().Where(s => s.DeckId == deckId).OrderBy(s => s.Position)
                                .Select(s => new { s.Text, s.SourceText, s.Position }).ToListAsync(ct);
        var matcher = new SentenceCueMatcher(japaneseCues);
        var aligner = englishCues == null ? null : new TranslationCueAligner(englishCues, englishOffset ?? 0, japaneseCues);
        int matched = 0, withEnglish = 0;
        var unmatched = new List<string>();
        foreach (var s in sentences)
        {
            var match = matcher.Locate(s.SourceText ?? s.Text, sentences.Count > 0 ? Math.Clamp(s.Position / (double)sentences.Count, 0, 1) : 0);
            if (match is not { } m)
            {
                if (unmatched.Count < 5) unmatched.Add(s.Text);
                continue;
            }

            matched++;
            if (aligner?.TextFor(japaneseCues[m.First].StartMs, japaneseCues[m.Last].EndMs, m.From, m.To) is { Length: > 0 })
                withEnglish++;
        }

        var (keptMined, keptFlags) = current == null ? (0, 0) : await scans.MinedAndFlagsAsync(deckId, ct);
        progress.Report = new EpisodeImportReport(deckId, sentences.Count, matched, withEnglish, unmatched, jaSource, englishSource,
                                                  fit.OffsetMs, how, audioOffset, englishOffset,
                                                  fit.Segments.Select(s => new TimingSegmentDto(s.StartMs, s.OffsetMs)).ToList(),
                                                  warnings, keptMined, keptFlags);
        return deckId;
    }

    /// <summary>Writes a chosen subtitle source to the episode's folder; returns the file and a description of it.</summary>
    private async Task<(string Path, string Source)> FetchAsync(string choice, string video, string dir, string language, CancellationToken ct)
    {
        var colon = choice.IndexOf(':');
        var (kind, value) = colon < 0 ? (choice, "") : (choice[..colon], choice[(colon + 1)..]);
        var release = Path.GetFileNameWithoutExtension(video);
        switch (kind)
        {
            case "embedded":
            {
                var index = int.Parse(value);
                var stream = (await VideoMedia.ListSubtitleStreams(video, ct)).FirstOrDefault(s => s.Index == index && s.IsText)
                             ?? throw new EpisodeImportException($"The video has no text subtitle stream #{index}.");
                // A stream tagged with the other language would put its lines among the sentences, or the English
                var wrong = language == "ja" ? EpisodeSources.IsEnglish(stream.Language) : EpisodeSources.IsJapanese(stream.Language);
                if (wrong)
                    throw new EpisodeImportException($"Subtitle stream #{index} is tagged {stream.Language}, not {(language == "ja" ? "Japanese" : "English")}.");
                var (ext, content) = await VideoMedia.ExtractSubtitle(video, stream, ct);
                var path = Path.Combine(dir, language + ext);
                await File.WriteAllTextAsync(path, content, ct);
                return (path, $"Embedded subtitle track #{index}{(stream.Title is { Length: > 0 } t ? $" ({t})" : "")}: {release}");
            }
            case "jimaku":
            {
                var uri = new Uri(value);
                if (uri.Scheme != "https" || !(uri.Host == "jimaku.cc" || uri.Host.EndsWith(".jimaku.cc")))
                    throw new EpisodeImportException("That Jimaku link isn't on jimaku.cc.");
                var name = Uri.UnescapeDataString(uri.Segments[^1]);
                var path = Path.Combine(dir, language + Path.GetExtension(name).ToLowerInvariant());
                await MetadataProviderHelper.JimakuDownloadFileAsync(httpClientFactory.CreateClient(), value, path);
                return (path, $"Jimaku: {name}");
            }
            case "sidecar":
            {
                if (!MediaRoots.Contains(config, value) || !File.Exists(value))
                    throw new EpisodeImportException($"{Path.GetFileName(value)} isn't under the media roots.");
                var path = Path.Combine(dir, language + Path.GetExtension(value).ToLowerInvariant());
                File.Copy(value, path, overwrite: true);
                return (path, $"Subtitle file: {Path.GetFileName(value)}");
            }
            default:
                throw new EpisodeImportException($"Unknown subtitle source \"{choice}\".");
        }
    }

    private static string Describe(TimingFit fit) =>
        string.Join(", then ", fit.Segments.Select(s => $"{s.OffsetMs:+#,0;-#,0} ms"));

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(Math.Max(ms, 0)).ToString(@"m\:ss");

    private static string ToSrt(IEnumerable<SubtitleItem> cues)
    {
        static string T(int ms) => TimeSpan.FromMilliseconds(Math.Max(ms, 0)).ToString(@"hh\:mm\:ss\,fff");
        var sb = new StringBuilder();
        var n = 1;
        foreach (var c in cues)
            sb.Append(n++).Append('\n').Append(T(c.StartMs)).Append(" --> ").Append(T(c.EndMs)).Append('\n').Append(c.Text).Append("\n\n");
        return sb.ToString();
    }
}
