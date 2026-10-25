using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Jiten.Core.Media;

/// <summary>ffmpeg/ffprobe calls for cutting card media out of local videos.</summary>
public static class VideoMedia
{
    /// <summary>The first audio stream tagged Japanese, else the first audio stream; null when the file has none.</summary>
    public static async Task<int?> FindJapaneseAudioStream(string videoPath, CancellationToken ct = default)
    {
        var json = await Run("ffprobe", ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=language", "-of", "json", videoPath], ct);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray()
                         .Select(s => (Index: s.GetProperty("index").GetInt32(),
                                       Lang: s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var l) ? l.GetString() : null))
                         .ToList();
        if (streams.Count == 0) return null;
        return streams.FirstOrDefault(s => s.Lang is "jpn" or "ja", streams[0]).Index;
    }

    /// <summary>Mono 8 kHz signed 16-bit PCM of one audio stream, for subtitle alignment.</summary>
    public static Task<byte[]> DecodePcm8k(string videoPath, int audioStream, CancellationToken ct = default) =>
        RunBytes("ffmpeg", ["-v", "error", "-i", videoPath, "-map", $"0:{audioStream}", "-ac", "1", "-ar", "8000", "-f", "s16le", "-"], ct);

    /// <summary>An MP3 of the audio between two video times.</summary>
    public static Task<byte[]> CutAudioMp3(string videoPath, int audioStream, int startMs, int endMs, CancellationToken ct = default) =>
        RunBytes("ffmpeg", ["-v", "error", "-ss", Seconds(startMs), "-i", videoPath, "-t", Seconds(endMs - startMs), "-map", $"0:{audioStream}",
                            "-vn", "-c:a", "libmp3lame", "-q:a", "4", "-f", "mp3", "-"], ct);

    /// <summary>A JPEG of the frame at a video time, scaled to <paramref name="height"/> pixels high.</summary>
    public static Task<byte[]> GrabFrameJpeg(string videoPath, int atMs, int height, CancellationToken ct = default) =>
        RunBytes("ffmpeg", ["-v", "error", "-ss", Seconds(atMs), "-i", videoPath, "-map", "0:v:0", "-frames:v", "1", "-vf", $"scale=-2:{height}",
                            "-q:v", "4", "-f", "image2", "-c:v", "mjpeg", "-"], ct);

    private static string Seconds(int ms) => (Math.Max(ms, 0) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<string> Run(string file, IEnumerable<string> args, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await RunBytes(file, args, ct));

    private static async Task<byte[]> RunBytes(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}");
        using var output = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} exited with {process.ExitCode}: {(await stderr).Trim()}");
        return output.ToArray();
    }
}
