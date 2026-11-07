using System.Diagnostics;
using FluentAssertions;
using Jiten.Core.Media;

namespace Jiten.Tests;

public class VideoMediaTests
{
    [Fact]
    public async Task CutVideoWebmMakesAPlayableClipWithSound()
    {
        var source = Path.Combine(Path.GetTempPath(), $"jiten-test-{Guid.NewGuid():N}.mkv");
        var clip = Path.Combine(Path.GetTempPath(), $"jiten-test-{Guid.NewGuid():N}.webm");
        try
        {
            // 6 s of 1080p test pattern with a tone: video is stream 0, audio stream 1
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=1920x1080:rate=24:duration=6", "-f", "lavfi", "-i",
                      "sine=frequency=440:duration=6", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-y", source);

            var bytes = await VideoMedia.CutVideoWebm(source, 1, 1000, 3500, 480);
            await File.WriteAllBytesAsync(clip, bytes);

            var probe = await Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,codec_name,height:format=duration", "-of",
                                  "default=noprint_wrappers=1", clip);
            probe.Should().Contain("codec_name=vp9").And.Contain("height=480").And.Contain("codec_name=opus");
            var duration = double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=")).Split('=')[1], System.Globalization.CultureInfo.InvariantCulture);
            duration.Should().BeApproximately(2.5, 0.2);
        }
        finally
        {
            File.Delete(source);
            File.Delete(clip);
        }
    }

    private static async Task<string> Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, error);
        return output;
    }
}
