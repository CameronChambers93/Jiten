using Jiten.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>Fixes users submitted for review, oldest first, each with the line as it is now (at most 50 a page: each looks up its cues).</summary>
    [HttpGet("sentence-submissions")]
    public async Task<IResult> GetSentenceSubmissions([FromServices] SentenceSubmissionService submissions, CancellationToken ct,
                                                      [FromQuery] int offset = 0, [FromQuery] int limit = 50) =>
        Results.Ok(await submissions.AdminListAsync(Math.Max(offset, 0), Math.Clamp(limit, 1, 50), ct));

    /// <summary>How many submitted fixes are waiting, for the dashboard.</summary>
    [HttpGet("sentence-submissions/count")]
    public async Task<IResult> CountSentenceSubmissions([FromServices] SentenceSubmissionService submissions, CancellationToken ct) =>
        Results.Ok(new { pending = await submissions.PendingCountAsync(ct) });

    /// <summary>Makes a submitted fix the shared line's.</summary>
    [HttpPost("sentence-submissions/{submissionId:long}/approve")]
    public async Task<IResult> ApproveSentenceSubmission([FromServices] SentenceSubmissionService submissions, [FromServices] ICurrentUserService userService,
                                                         long submissionId, CancellationToken ct)
    {
        try
        {
            return await submissions.ApproveAsync(submissionId, userService.UserId!, ct)
                ? Results.NoContent()
                : Results.NotFound(new { message = "This fix isn't waiting for review, or its line is gone." });
        }
        catch (SentenceFixException e)
        {
            return Results.BadRequest(new { message = e.Message });
        }
    }

    /// <summary>Turns down a submitted fix, with an optional reason sent to its submitter.</summary>
    [HttpPost("sentence-submissions/{submissionId:long}/reject")]
    public async Task<IResult> RejectSentenceSubmission([FromServices] SentenceSubmissionService submissions, [FromServices] ICurrentUserService userService,
                                                        long submissionId, [FromBody] RejectSubmissionRequest request, CancellationToken ct)
    {
        if (request.Note?.Length > SentenceSubmissionService.MaxNoteLength)
            return Results.BadRequest(new { message = $"The reason is at most {SentenceSubmissionService.MaxNoteLength} characters." });
        return await submissions.RejectAsync(submissionId, userService.UserId!, request.Note, ct)
            ? Results.NoContent()
            : Results.NotFound(new { message = "This fix isn't waiting for review." });
    }
}

public record RejectSubmissionRequest(string? Note);
