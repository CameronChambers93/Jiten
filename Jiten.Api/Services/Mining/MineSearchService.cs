using System.Text.Json;
using System.Text.Json.Serialization;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Mining;

/// <summary>What "Run on all results" does for a saved search (GDR-059).</summary>
/// <param name="PipelineId">One of the user's pipelines; a pipeline that no longer exists falls back to the Mine page's default in the browser.</param>
/// <param name="PerWord">Sentences per word, 1 to 5.</param>
/// <param name="WhenAsked">existing, new or skip: the answer to an Anki step that would ask.</param>
public record MineSearchRunDto(string? PipelineId, int PerWord = 1, string? WhenAsked = null);

/// <summary>A named Mine page search: the page's URL query keys, and optionally its run settings.</summary>
public record MineSearchDto(string? Id, string? Name, Dictionary<string, string>? Query, MineSearchRunDto? Run = null);

public class MineSearchException(string message) : Exception(message);

/// <summary>The user's saved Mine searches, kept on the server so the phone and the PC see the same list (like the media filter presets).</summary>
public class MineSearchService(UserDbContext userContext)
{
    public const int MaxSearches = 50;
    public const int MaxNameLength = 40;
    public const int MaxValueLength = 500;
    public const int MaxIdLength = 100;

    /// <summary>The Mine page's URL keys (<c>filterQuery()</c> in <c>mine.vue</c>); mirrored in <c>utils/mineSearches.ts</c>.</summary>
    public static readonly HashSet<string> QueryKeys =
    [
        "titles", "episodes", "dict", "sort", "desc", "fmin", "fmax", "ranked", "lmin", "lmax", "umin", "umax", "lrmin", "lrmax",
        "status", "others", "types", "script", "en", "audio", "anki", "deck", "layout"
    ];

    public static readonly string[] WhenAskedValues = ["existing", "new", "skip"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<List<MineSearchDto>> GetAsync(string userId, CancellationToken ct)
    {
        var json = await userContext.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.MineSearchesJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MineSearchDto>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Replaces every saved search with the cleaned list. Throws <see cref="MineSearchException"/> for a list that can't be cleaned.</summary>
    public async Task<List<MineSearchDto>> SaveAsync(string userId, List<MineSearchDto> searches, CancellationToken ct)
    {
        var clean = Clean(searches);
        var settings = userContext.UserSettings.Local.FirstOrDefault(s => s.UserId == userId)
                       ?? await userContext.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (settings == null) userContext.UserSettings.Add(settings = new UserSettings { UserId = userId });
        settings.MineSearchesJson = JsonSerializer.Serialize(clean, Json);
        await userContext.SaveChangesAsync(ct);
        return clean;
    }

    /// <summary>
    /// Names are needed, at most 40 characters and unique ignoring case; ids must differ (a missing one is made). Unknown
    /// keys, blank values and values over 500 characters are dropped, as the media filter presets do; run settings are
    /// clamped to their ranges.
    /// </summary>
    public static List<MineSearchDto> Clean(List<MineSearchDto>? searches)
    {
        searches ??= [];
        if (searches.Count > MaxSearches) throw new MineSearchException($"At most {MaxSearches} saved searches.");

        var ids = new HashSet<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new List<MineSearchDto>(searches.Count);
        foreach (var search in searches)
        {
            var name = search.Name?.Trim() ?? "";
            if (name.Length == 0) throw new MineSearchException("A saved search needs a name.");
            if (name.Length > MaxNameLength) throw new MineSearchException($"Names go up to {MaxNameLength} characters.");
            if (!names.Add(name)) throw new MineSearchException($"Two saved searches are called \"{name}\".");

            var id = string.IsNullOrWhiteSpace(search.Id) ? Guid.NewGuid().ToString() : search.Id.Trim();
            if (id.Length > MaxIdLength) throw new MineSearchException("A saved search's id is too long.");
            if (!ids.Add(id)) throw new MineSearchException("Two saved searches have the same id.");

            var query = (search.Query ?? [])
                        .Where(kv => QueryKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value) && kv.Value.Length <= MaxValueLength)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);

            MineSearchRunDto? run = null;
            if (search.Run is { } r)
                run = new MineSearchRunDto(string.IsNullOrWhiteSpace(r.PipelineId) || r.PipelineId.Trim().Length > MaxIdLength ? null : r.PipelineId.Trim(),
                                           Math.Clamp(r.PerWord, 1, MiningSentenceService.SentencesPerWord),
                                           WhenAskedValues.Contains(r.WhenAsked) ? r.WhenAsked : "existing");

            clean.Add(new MineSearchDto(id, name, query, run));
        }

        return clean;
    }
}
