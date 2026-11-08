using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Sentence audio, screenshots and video clips cut from the local video a subtitle deck was linked to, for mining cards.
/// The caller's fix of a line (else the shared one) decides the clip and frame; the fix screen previews other times by
/// passing them, in subtitle time.
/// </summary>
[ApiController]
[Route("api/sentence-media")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceMediaController(ISentenceMediaService sentenceMedia, SentenceFixStore fixes, ICurrentUserService currentUser) : ControllerBase
{
    private const int MaxPadMs = 5000;
    private const int MaxPreviewMs = 30_000;
    private const string CacheControl = "private, max-age=86400";

    [HttpGet("{sentenceId:long}/audio")]
    [SwaggerOperation(Summary = "MP3 of the sentence's clip: its fixed range, or its subtitle cues padded on both sides")]
    public async Task<IResult> GetAudio(long sentenceId, [FromQuery] int padStartMs = 250, [FromQuery] int padEndMs = 250,
                                        [FromQuery] int? startMs = null, [FromQuery] int? endMs = null, CancellationToken ct = default)
    {
        if (BadPreview(startMs, endMs) is { } bad) return bad;
        var clip = await sentenceMedia.ResolveAsync(sentenceId, await TimesAsync(sentenceId, startMs, endMs, null, ct), ct);
        if (clip == null) return Results.NotFound(new { error = "No linked video for this sentence" });

        var audio = await sentenceMedia.CutAudioAsync(clip, Math.Clamp(padStartMs, 0, MaxPadMs), Math.Clamp(padEndMs, 0, MaxPadMs), ct);
        return Results.File(audio, "audio/mpeg");
    }

    [HttpGet("{sentenceId:long}/picture")]
    [SwaggerOperation(Summary = "JPEG of the sentence's frame: the fixed one, or the middle of its clip")]
    public async Task<IResult> GetPicture(long sentenceId, [FromQuery] int? atMs = null, CancellationToken ct = default)
    {
        if (atMs < 0) return Results.BadRequest(new { error = "atMs can't be negative" });
        var clip = await sentenceMedia.ResolveAsync(sentenceId, await TimesAsync(sentenceId, null, null, atMs, ct), ct);
        if (clip == null) return Results.NotFound(new { error = "No linked video for this sentence" });

        // Lists show these as thumbnails, so a repeat view shouldn't start ffmpeg again; a fix changes the URL (see the page's v=)
        var picture = await sentenceMedia.GrabPictureAsync(clip, ct);
        Response.Headers.CacheControl = CacheControl;
        return Results.File(picture, "image/jpeg");
    }

    [HttpGet("{sentenceId:long}/video")]
    [SwaggerOperation(Summary = "WebM (VP9, Opus) with sound of the sentence's clip, cut like the audio")]
    public async Task<IResult> GetVideo(long sentenceId, [FromQuery] int padStartMs = 250, [FromQuery] int padEndMs = 250,
                                        [FromQuery] int? startMs = null, [FromQuery] int? endMs = null, CancellationToken ct = default)
    {
        if (BadPreview(startMs, endMs) is { } bad) return bad;
        var clip = await sentenceMedia.ResolveAsync(sentenceId, await TimesAsync(sentenceId, startMs, endMs, null, ct), ct);
        if (clip == null) return Results.NotFound(new { error = "No linked video for this sentence" });

        var video = await sentenceMedia.CutVideoAsync(clip, Math.Clamp(padStartMs, 0, MaxPadMs), Math.Clamp(padEndMs, 0, MaxPadMs), ct);
        Response.Headers.CacheControl = CacheControl;
        return Results.File(video, "video/webm");
    }

    private static IResult? BadPreview(int? startMs, int? endMs)
    {
        if (startMs == null && endMs == null) return null;
        if (startMs is not { } start || endMs is not { } end) return Results.BadRequest(new { error = "Pass both startMs and endMs" });
        if (start < 0 || end <= start || end - start > MaxPreviewMs)
            return Results.BadRequest(new { error = $"A clip runs forward and lasts at most {MaxPreviewMs / 1000} s" });
        return null;
    }

    /// <summary>Previewed times win; the rest come from the caller's fix of the line, else the shared fix, else the cues.</summary>
    private async Task<ClipTimes?> TimesAsync(long sentenceId, int? startMs, int? endMs, int? atMs, CancellationToken ct)
    {
        var fixedTimes = ClipTimes.Of(await fixes.ForUserAsync(currentUser.UserId, sentenceId, ct));
        if (startMs != null) return new ClipTimes(startMs, endMs, null);
        if (atMs != null) return new ClipTimes(fixedTimes?.StartMs, fixedTimes?.EndMs, atMs);
        return fixedTimes;
    }
}
