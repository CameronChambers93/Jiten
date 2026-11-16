using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public static class SentenceCheckStatus
{
    /// <summary>Handed to a checker session; free again after <see cref="SentenceCheck.LeasedUntil"/>.</summary>
    public const string Leased = "leased";
    public const string Passed = "passed";
    public const string Flagged = "flagged";
    /// <summary>An admin accepted (possibly edited) a suggestion; counts as checked.</summary>
    public const string Accepted = "accepted";
    /// <summary>An admin said the flag was not a problem; counts as checked.</summary>
    public const string Dismissed = "dismissed";

    public static readonly string[] Done = [Passed, Accepted, Dismissed];
}

public static class SentenceCheckKind
{
    /// <summary>Subtitle English belongs to a neighbour, is cut short, or covers more than the line.</summary>
    public const string EnglishFit = "english-fit";
    /// <summary>AI English is wrong or misses meaning, tone or names.</summary>
    public const string EnglishQuality = "english-quality";
    /// <summary>The Japanese has subtitle, OCR or typing mistakes, or stray markup.</summary>
    public const string Japanese = "japanese";
    /// <summary>A fragment, or two unrelated sentences together: report only.</summary>
    public const string Split = "split";

    public static readonly string[] All = [EnglishFit, EnglishQuality, Japanese, Split];
}

/// <param name="Kind">One of <see cref="SentenceCheckKind"/>.</param>
public record SentenceCheckFinding(string Kind, string Note, string? SuggestedEnglish = null, string? SuggestedText = null);

/// <summary>
/// The result of checking one deck line (occurrence) for bulk translation quality. It holds only while the line's text
/// and English are still <see cref="CheckedText"/> and <see cref="CheckedEnglish"/>; otherwise the line is to do again.
/// Findings are suggestions: nothing changes until an admin accepts one.
/// </summary>
public class SentenceCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public long ExampleSentenceId { get; set; }

    /// <summary>The deck (episode) of the line when it was leased, for filtering the queue by title.</summary>
    public int DeckId { get; set; }

    public string CheckedText { get; set; } = "";
    public string? CheckedEnglish { get; set; }

    /// <summary>"subtitles", "ai" or "user" (a shared fix), as <c>TranslationOrigin</c>.</summary>
    public string? EnglishOrigin { get; set; }

    public string Status { get; set; } = SentenceCheckStatus.Leased;
    public DateTime? LeasedUntil { get; set; }

    public string FindingsJson { get; set; } = "[]";

    /// <summary>Who checked it, e.g. "claude-code/sonnet rubric 1".</summary>
    public string? Checker { get; set; }

    public DateTime? CheckedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }

    public List<SentenceCheckFinding> GetFindings() => JsonSerializer.Deserialize<List<SentenceCheckFinding>>(FindingsJson, JsonOptions) ?? [];

    public void SetFindings(IEnumerable<SentenceCheckFinding> findings) => FindingsJson = JsonSerializer.Serialize(findings.ToList(), JsonOptions);
}
