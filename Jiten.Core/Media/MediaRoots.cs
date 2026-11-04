using Microsoft.Extensions.Configuration;

namespace Jiten.Core.Media;

/// <summary>
/// The folders linked videos may be opened from. Video paths come from metadata.json, so nothing outside them is
/// ever read. <c>MediaRoots</c> is a list (e.g. the library mount plus a folder of downloaded YouTube videos);
/// the older single <c>MediaRoot</c> still counts. With neither set, only <c>/media</c>.
/// </summary>
public static class MediaRoots
{
    public static IReadOnlyList<string> From(IConfiguration configuration)
    {
        var roots = configuration.GetSection("MediaRoots").GetChildren()
                                 .Select(c => c.Value)
                                 .Append(configuration["MediaRoot"])
                                 .Where(r => !string.IsNullOrWhiteSpace(r))
                                 .Select(r => Path.GetFullPath(r!).TrimEnd('/') + "/")
                                 .Distinct()
                                 .ToList();
        return roots.Count > 0 ? roots : ["/media/"];
    }

    public static bool Contains(IConfiguration configuration, string path)
    {
        var full = Path.GetFullPath(path);
        return From(configuration).Any(root => full.StartsWith(root, StringComparison.Ordinal));
    }
}
