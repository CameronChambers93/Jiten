using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

/// <summary>Links inserted subtitle decks to their local videos, so the API can cut sentence audio and screenshots.</summary>
public class VideoCommands(CliContext context)
{
    /// <summary>
    /// For every deck folder under <c>--link-video</c>: finds the inserted deck by its metadata.json title, then for each child
    /// with a VideoPath stores the subtitle cues (with times) and the video source, detecting the subtitle offset unless given.
    /// </summary>
    public async Task LinkVideo(CliOptions options)
    {
        foreach (var directory in Directory.GetDirectories(options.LinkVideo!).Order())
        {
            var metadataPath = Path.Combine(directory, "metadata.json");
            if (!File.Exists(metadataPath)) continue;

            var metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(metadataPath));
            if (metadata == null || metadata.Children.All(c => string.IsNullOrEmpty(c.VideoPath))) continue;

            await LinkDeck(directory, metadata, options);
        }
    }

    private async Task LinkDeck(string directory, Metadata metadata, CliOptions options)
    {
        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var parents = await db.Decks.Where(d => d.ParentDeckId == null && d.OriginalTitle == metadata.OriginalTitle)
                              .Select(d => d.DeckId).ToListAsync();
        if (parents.Count != 1)
        {
            Console.WriteLine($"{metadata.OriginalTitle}: expected one inserted deck with this title, found {parents.Count}; skipping.");
            return;
        }

        // Children are inserted in metadata order, so DeckOrder follows it.
        var children = await db.Decks.Where(d => d.ParentDeckId == parents[0]).OrderBy(d => d.DeckOrder)
                               .Select(d => new { d.DeckId, d.OriginalTitle }).ToListAsync();
        if (children.Count != metadata.Children.Count)
        {
            Console.WriteLine($"{metadata.OriginalTitle}: metadata.json has {metadata.Children.Count} children, the database {children.Count}; skipping.");
            return;
        }

        Console.WriteLine($"{metadata.OriginalTitle} (deck {parents[0]}){(options.DryRun ? " [dry run]" : "")}");
        var extractor = new SubtitleExtractor();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(options.Threads, 1) };
        var results = new (int DeckId, DeckSubtitleTrack Track, DeckVideoSource Source, string Line)?[children.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, children.Count), parallel, async (i, ct) =>
        {
            var child = children[i];
            var meta = metadata.Children[i];
            if (string.IsNullOrEmpty(meta.VideoPath)) return;

            if (meta.OriginalTitle != child.OriginalTitle)
            {
                Console.WriteLine($"  {child.OriginalTitle}: metadata.json has \"{meta.OriginalTitle}\" here; skipping.");
                return;
            }

            if (!File.Exists(meta.VideoPath))
            {
                Console.WriteLine($"  {child.OriginalTitle}: video not found: {meta.VideoPath}");
                return;
            }

            var subtitlePath = ResolveFile(directory, meta.FilePath);
            if (subtitlePath == null)
            {
                Console.WriteLine($"  {child.OriginalTitle}: subtitle file not found: {meta.FilePath}");
                return;
            }

            var items = await extractor.ExtractItems(subtitlePath);
            var audioStream = meta.AudioStream ?? await VideoMedia.FindJapaneseAudioStream(meta.VideoPath, ct);
            if (audioStream == null)
            {
                Console.WriteLine($"  {child.OriginalTitle}: no audio stream in {meta.VideoPath}");
                return;
            }

            string how;
            int offset;
            if (meta.SubtitleOffsetMs is { } given)
            {
                offset = given;
                how = "from metadata.json";
            }
            else
            {
                var pcm = await VideoMedia.DecodePcm8k(meta.VideoPath, audioStream.Value, ct);
                var detected = SubtitleOffsetDetector.Detect(pcm, items.Select(it => (it.StartMs, it.EndMs)));
                offset = detected.OffsetMs;
                how = $"detected, fit {detected.Score:0} vs {detected.ScoreAtZero:0} unshifted";
            }

            results[i] = (child.DeckId, DeckSubtitleTrack.FromItems(items),
                          new DeckVideoSource { DeckId = child.DeckId, VideoPath = meta.VideoPath, AudioStreamIndex = audioStream.Value, SubtitleOffsetMs = offset },
                          $"  {child.OriginalTitle}: {items.Count} cues, audio stream {audioStream}, offset {offset:+0;-0} ms ({how})");
        });

        foreach (var result in results.OfType<(int DeckId, DeckSubtitleTrack Track, DeckVideoSource Source, string Line)>())
        {
            Console.WriteLine(result.Line);
            if (options.DryRun) continue;

            var track = await db.DeckSubtitleTracks.FindAsync(result.DeckId);
            if (track == null)
                db.DeckSubtitleTracks.Add(new DeckSubtitleTrack { DeckId = result.DeckId, CuesJson = result.Track.CuesJson });
            else
                track.CuesJson = result.Track.CuesJson;

            var source = await db.DeckVideoSources.FindAsync(result.DeckId);
            if (source == null)
                db.DeckVideoSources.Add(result.Source);
            else
            {
                source.VideoPath = result.Source.VideoPath;
                source.AudioStreamIndex = result.Source.AudioStreamIndex;
                source.SubtitleOffsetMs = result.Source.SubtitleOffsetMs;
            }
        }

        if (!options.DryRun)
            Console.WriteLine($"  Saved {await db.SaveChangesAsync()} rows.");
    }

    /// <summary>Same lookup as --parse: the path as given, else the file name inside the deck folder.</summary>
    private static string? ResolveFile(string directory, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        if (File.Exists(filePath)) return filePath;
        var local = Path.Combine(directory, Path.GetFileName(filePath));
        return File.Exists(local) ? local : null;
    }
}
