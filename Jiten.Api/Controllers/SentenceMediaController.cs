using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>Sentence audio and screenshots cut from the local video a subtitle deck was linked to, for mining cards.</summary>
[ApiController]
[Route("api/sentence-media")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SentenceMediaController(ISentenceMediaService sentenceMedia) : ControllerBase
{
    private const int MaxPadMs = 5000;

    [HttpGet("{sentenceId:long}/audio")]
    [SwaggerOperation(Summary = "MP3 of the sentence's subtitle cues, padded on both sides")]
    public async Task<IResult> GetAudio(long sentenceId, [FromQuery] int padStartMs = 250, [FromQuery] int padEndMs = 250, CancellationToken ct = default)
    {
        var clip = await sentenceMedia.ResolveAsync(sentenceId, ct);
        if (clip == null) return Results.NotFound(new { error = "No linked video for this sentence" });

        var audio = await sentenceMedia.CutAudioAsync(clip, Math.Clamp(padStartMs, 0, MaxPadMs), Math.Clamp(padEndMs, 0, MaxPadMs), ct);
        return Results.File(audio, "audio/mpeg");
    }

    [HttpGet("{sentenceId:long}/picture")]
    [SwaggerOperation(Summary = "JPEG of the frame in the middle of the sentence")]
    public async Task<IResult> GetPicture(long sentenceId, CancellationToken ct = default)
    {
        var clip = await sentenceMedia.ResolveAsync(sentenceId, ct);
        if (clip == null) return Results.NotFound(new { error = "No linked video for this sentence" });

        return Results.File(await sentenceMedia.GrabPictureAsync(clip, ct), "image/jpeg");
    }
}
