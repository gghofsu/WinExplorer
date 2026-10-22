using System.Windows.Automation;
using WinExplorer.Core;

namespace WinExplorer.Shell;

/// <summary>
/// The visual order of an Explorer window's tabs, as the user sees them.
/// </summary>
/// <remarks>
/// <para>
/// The window's child handles are in z-order (the active tab is first), not in strip order, so
/// they cannot be used to decide where a tab has to be inserted. The tab strip itself is a UI
/// Automation element that lists its items in visual order, which is what the ordering rules
/// need. Results are cached for a moment because a single open asks several times.
/// </para>
/// <para>Only titles are read - never a folder - so this is cheap and cannot navigate anything.</para>
/// </remarks>
internal static class TabStripOrder
{
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Entry> Cache = new();

    /// <summary>How long a UI Automation read may take before it is abandoned.</summary>
    private const int ReadTimeoutMs = 700;

    private sealed record Entry(long Tick, List<string> Titles, bool Failed);

    /// <summary>Tab titles in strip order (left to right). Empty when the strip is unknown.</summary>
    public static List<string> Titles(nint windowHwnd, int maxAgeMs = 400)
    {
        if (windowHwnd == 0)
            return new List<string>();

        lock (Gate)
        {
            if (Cache.TryGetValue(windowHwnd, out var cached))
            {
                // A failed read is retried less eagerly than a successful one.
                var age = Environment.TickCount64 - cached.Tick;
                if (age < (cached.Failed ? 1500 : maxAgeMs))
                    return cached.Titles;
            }
        }

        var titles = ReadWithTimeout(windowHwnd, out var failed);

        lock (Gate)
        {
            Cache[windowHwnd] = new Entry(Environment.TickCount64, titles, failed);

            if (Cache.Count > 32)
            {
                foreach (var stale in Cache.Where(p => Environment.TickCount64 - p.Value.Tick > 10000)
                             .Select(p => p.Key).ToList())
                {
                    Cache.Remove(stale);
                }
            }
        }

        return titles;
    }

    /// <summary>Forgets the cached order of a window (after opening/closing a tab).</summary>
    public static void Invalidate(nint windowHwnd = 0)
    {
        lock (Gate)
        {
            if (windowHwnd == 0)
                Cache.Clear();
            else
                Cache.Remove(windowHwnd);
        }
    }

    /// <summary>
    /// Position of the tab that shows <paramref name="location"/>, or -1 when it cannot be
    /// determined (no titles, or an ambiguous title).
    /// </summary>
    public static int IndexOf(nint windowHwnd, string displayName)
    {
        var titles = Titles(windowHwnd);
        if (titles.Count == 0 || displayName.Length == 0)
            return -1;

        var match = -1;
        for (var i = 0; i < titles.Count; i++)
        {
            if (!string.Equals(titles[i], displayName, StringComparison.OrdinalIgnoreCase))
                continue;

            // The same name twice means the position is ambiguous; the caller must not guess.
            if (match >= 0)
                return -1;

            match = i;
        }

        return match;
    }

    /// <summary>
    /// The text Explorer puts on a tab for <paramref name="location"/>. The special folders are
    /// named explicitly because the namespace ids have no folder name of their own.
    /// </summary>
    public static string TitleOf(string? location)
    {
        return SpecialFolders.Rank(location) switch
        {
            0 => "Home",
            1 => "This PC",
            2 => "Desktop",
            3 => "Downloads",
            _ => PathNormalizer.ToDisplayName(location)
        };
    }

    /// <summary>
    /// Position where a tab showing <paramref name="location"/> belongs so that the special
    /// folders stay at the left in their fixed order; -1 when it simply belongs at the end.
    /// </summary>
    public static int InsertionPosition(nint windowHwnd, string location)
    {
        var rank = SpecialFolders.Rank(location);
        if (rank is null)
            return -1;

        var titles = Titles(windowHwnd);
        if (titles.Count == 0)
            return rank.Value; // no order available: put it as far left as its rank suggests

        return SpecialFolders.PositionIn(titles, location);
    }

    /// <summary>
    /// Reads the tab strip off the UI Automation tree, abandoned after a moment: an Explorer
    /// window that is busy must not stall the open it is part of.
    /// </summary>
    private static List<string> ReadWithTimeout(nint windowHwnd, out bool failed)
    {
        var read = Task.Run(() => Read(windowHwnd));

        try
        {
            if (read.Wait(ReadTimeoutMs))
            {
                failed = false;
                return read.Result;
            }
        }
        catch (Exception ex)
        {
            // Read already swallows its own errors; this is belt and braces.
            System.Diagnostics.Debug.WriteLine($"Tab strip read failed: {ex.Message}");
        }

        failed = true;
        return new List<string>();
    }

    private static List<string> Read(nint windowHwnd)
    {
        var titles = new List<string>();
        try
        {
            var window = AutomationElement.FromHandle(windowHwnd);
            if (window is null)
                return titles;

            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            var items = window.FindAll(TreeScope.Descendants, condition);
            for (var i = 0; i < items.Count; i++)
            {
                try
                {
                    titles.Add(items[i].Current.Name ?? string.Empty);
                }
                catch
                {
                    titles.Add(string.Empty);
                }
            }
        }
        catch
        {
            // UIA is best effort: the caller falls back to appending.
        }

        return titles;
    }
}
