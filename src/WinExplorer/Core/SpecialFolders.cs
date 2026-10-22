using System.Runtime.InteropServices;
using WinExplorer.Interop;

namespace WinExplorer.Core;

/// <summary>
/// The folders that are always kept at the left of the tab strip, in a fixed order:
/// Home, This PC, Desktop, Downloads.
/// </summary>
/// <remarks>
/// A folder is recognised both by its shell namespace id (Home, This PC, and the Desktop /
/// Downloads known folders) and by its path (a redirected or synced Desktop is the same
/// folder as far as the user is concerned), so the rule holds on machines with OneDrive or a
/// moved profile folder.
/// </remarks>
public static class SpecialFolders
{
    /// <summary>Home, the first tab of a fresh Explorer window.</summary>
    public const string Home = PathNormalizer.Home;

    private const string DesktopNamespace = "shell:::{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}";
    private const string DownloadsNamespace = "shell:::{374DE290-123F-4565-9164-39C4925E467B}";

    private static readonly Guid FolderIdDesktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? _desktopPath;
    private static string? _downloadsPath;

    /// <summary>
    /// Position of <paramref name="location"/> in the fixed order (0 = Home, 3 = Downloads),
    /// or null when the folder is not one of them.
    /// </summary>
    public static int? Rank(string? location)
    {
        var value = PathNormalizer.Normalize(location);
        if (value.Length == 0)
            return null;

        if (StartsWith(value, PathNormalizer.Home))
            return 0;

        if (StartsWith(value, PathNormalizer.ThisPc))
            return 1;

        if (StartsWith(value, DesktopNamespace) || SamePath(value, DesktopPath()))
            return 2;

        if (StartsWith(value, DownloadsNamespace) || SamePath(value, DownloadsPath()))
            return 3;

        return null;
    }

    /// <summary>True when the folder belongs to the fixed group.</summary>
    public static bool IsSpecial(string? location) => Rank(location) is not null;

    /// <summary>The name used in messages, or null.</summary>
    public static string? Describe(string? location) => Rank(location) switch
    {
        0 => "Home",
        1 => "This PC",
        2 => "Desktop",
        3 => "Downloads",
        _ => null
    };

    /// <summary>
    /// Where a folder belongs in a strip with the given tab titles: the position of the first
    /// tab that is a normal folder or a special folder that comes after it. Empty strings are
    /// tabs whose title is not known yet (treated as normal folders).
    /// </summary>
    /// <returns>The 0-based position, or -1 when the folder simply belongs at the end.</returns>
    public static int PositionIn(IReadOnlyList<string> titles, string? location)
    {
        var rank = Rank(location);
        if (rank is null)
            return -1;

        for (var i = 0; i < titles.Count; i++)
        {
            var otherRank = RankOfTitle(titles[i]);
            if (otherRank is null || otherRank > rank)
                return i;
        }

        return titles.Count;
    }

    /// <summary>Rank of a tab title ("Home", "This PC", "Desktop", "Downloads"), or null.</summary>
    public static int? RankOfTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var value = title.Trim();

        if (string.Equals(value, "Home", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(value, "This PC", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (string.Equals(value, "Desktop", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (string.Equals(value, "Downloads", StringComparison.OrdinalIgnoreCase))
            return 3;

        return null;
    }

    /// <summary>The user's Desktop folder as Explorer shows it.</summary>
    public static string DesktopPath() => _desktopPath ??= KnownFolder(FolderIdDesktop) ?? string.Empty;

    /// <summary>The user's Downloads folder.</summary>
    public static string DownloadsPath() => _downloadsPath ??= KnownFolder(FolderIdDownloads) ?? string.Empty;

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(ref id, 0, 0, out var pointer) != 0 || pointer == 0)
                return null;

            try
            {
                var path = Marshal.PtrToStringUni(pointer);
                return string.IsNullOrWhiteSpace(path) ? null : PathNormalizer.Normalize(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string value, string path)
    {
        if (path.Length == 0)
            return false;

        return string.Equals(value.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
