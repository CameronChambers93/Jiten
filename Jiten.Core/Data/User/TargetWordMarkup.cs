using System.Text.RegularExpressions;

namespace Jiten.Core.Data.User;

/// <summary>
/// How a sentence marks its target word on the user's cards. The store always holds the word in <c>&lt;b&gt;</c>; the
/// user's template replaces that when fields are written out.
/// </summary>
public static partial class TargetWordMarkup
{
    public const string Default = "<b>{word}</b>";
    public const string Placeholder = "{word}";
    public const int MaxTemplateLength = 200;

    [GeneratedRegex("<b>(.*?)</b>", RegexOptions.Singleline)]
    private static partial Regex Stored();

    [GeneratedRegex("""<span class="cloze_body">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex Cloze();

    /// <summary>A template holds the word exactly once.</summary>
    public static bool IsValid(string template) =>
        template.Length <= MaxTemplateLength && template.Split(Placeholder).Length == 2;

    /// <summary>A note's field as the store holds it. Anki notes mark the word with either tag, depending on what made them.</summary>
    public static string ToStored(string html) => Cloze().Replace(html, "<b>$1</b>");

    public static string Apply(string html, string template) =>
        template == Default ? html : Stored().Replace(html, m => template.Replace(Placeholder, m.Groups[1].Value));
}
