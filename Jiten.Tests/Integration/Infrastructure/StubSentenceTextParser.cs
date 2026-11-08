using Jiten.Api.Helpers;
using Jiten.Core.Data;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>
/// The parser needs its dictionaries and Redis, which the tests don't have: this one finds each <see cref="Words"/> entry
/// in the text, as a content word.
/// </summary>
public class StubSentenceTextParser : ISentenceTextParser
{
    /// <summary>Surface → word id.</summary>
    public static readonly Dictionary<string, int> Words = new() { ["頭"] = 1001, ["髪"] = 1002 };

    /// <summary>Text the parser fails on, like a Redis outage would.</summary>
    public const string Fails = "壊れた";

    public Task<List<SentenceToken>> ParseAsync(string text, CancellationToken ct) =>
        text.Contains(Fails) ? Task.FromException<List<SentenceToken>>(new InvalidOperationException("parser down")) :
        Task.FromResult(Words.Select(w => (w.Value, Position: text.IndexOf(w.Key, StringComparison.Ordinal), w.Key.Length))
                             .Where(w => w.Position >= 0)
                             .OrderBy(w => w.Position)
                             .Select(w => new SentenceToken(w.Value, 0, (byte)w.Position, (byte)w.Length, IsTarget: false, IsFunctionWord: false))
                             .ToList());
}
