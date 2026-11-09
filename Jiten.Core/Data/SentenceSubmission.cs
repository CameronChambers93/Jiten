namespace Jiten.Core.Data;

/// <summary>
/// A user's fix of a shared line, submitted for an admin to approve (sentence flags step 4). A snapshot: the user's own fix
/// stays theirs, and they can keep editing it while this waits.
/// </summary>
public class SentenceSubmission
{
    public long SentenceSubmissionId { get; set; }
    public long ExampleSentenceId { get; set; }

    /// <summary>Who submitted it. No foreign key: users are in another schema.</summary>
    public required string UserId { get; set; }

    /// <summary>The proposed parts; null leaves that part of the line as it is.</summary>
    public string? Text { get; set; }

    public string? English { get; set; }
    public int? StartMs { get; set; }
    public int? EndMs { get; set; }
    public int? PictureMs { get; set; }

    /// <summary>Other lines with the same text to give the fixed text too.</summary>
    public long[] SameTextIds { get; set; } = [];

    /// <summary>The line's latest correction when this was submitted, to tell whether the line changed since.</summary>
    public long? BaseCorrectionId { get; set; }

    /// <summary>See <see cref="SentenceSubmissionStatus"/>.</summary>
    public string Status { get; set; } = SentenceSubmissionStatus.Pending;

    public string? ReviewerId { get; set; }
    public DateTime? ReviewedAt { get; set; }

    /// <summary>The admin's reason for a rejection, sent to the submitter.</summary>
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class SentenceSubmissionStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
}
