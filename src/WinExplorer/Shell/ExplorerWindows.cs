using WinExplorer.Core;
using WinExplorer.Interop;
using WinExplorer.Services;

namespace WinExplorer.Shell;

/// <summary>A tab inside a File Explorer window.</summary>
internal sealed record ExplorerTab(nint TabHwnd, nint WindowHwnd, string Location, string DisplayName);

/// <summary>A File Explorer top level window and its tabs.</summary>
internal sealed record ExplorerWindow(nint Hwnd, IReadOnlyList<ExplorerTab> Tabs)
{
    public bool IsValid => Hwnd != 0 && NativeMethods.IsWindow(Hwnd);

    /// <summary>The tab the user is looking at (first in z-order).</summary>
    public nint ActiveTab => Tabs.Count > 0 ? Tabs[0].TabHwnd : 0;

    public string ActiveLocation => Tabs.Count > 0 ? Tabs[0].Location : string.Empty;
}

/// <summary>Finding File Explorer windows and tabs, and (cheaply) what they show.</summary>
internal static class ExplorerWindows
{
    private static readonly object CacheGate = new();
    private static (long Tick, Dictionary<nint, ShellTabLocation> Map) _cache = (0, new());

    /// <summary>All top level File Explorer windows, front to back.</summary>
    public static List<nint> GetWindows()
    {
        var windows = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
                windows.Add(hwnd);
            return true;
        }, 0);
        return windows;
    }

    /// <summary>Tab windows of one Explorer window, most recently used first.</summary>
    public static List<nint> GetTabs(nint windowHwnd)
    {
        var tabs = new List<nint>();
        if (windowHwnd == 0)
            return tabs;

        nint current = 0;
        while (true)
        {
            current = NativeMethods.FindWindowEx(windowHwnd, current, NativeMethods.ExplorerTabClass, null);
            if (current == 0)
                break;
            tabs.Add(current);
        }

        return tabs;
    }

    /// <summary>The tab the user is looking at: Explorer keeps it at the top of the z-order.</summary>
    public static nint GetActiveTab(nint windowHwnd) => GetTabs(windowHwnd).FirstOrDefault();

    /// <summary>Walks up from a child window (a view) to its tab and window.</summary>
    public static (nint Tab, nint Window) GetTabAndWindowFromChild(nint childHwnd)
    {
        var current = NativeMethods.GetAncestor(childHwnd, NativeMethods.GA_PARENT);
        while (current != 0)
        {
            if (NativeMethods.HasClassName(current, NativeMethods.ExplorerTabClass))
            {
                var window = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
                if (!NativeMethods.HasClassName(window, NativeMethods.ExplorerWindowClass))
                    window = NativeMethods.GetAncestor(current, NativeMethods.GA_ROOT);
                return (current, window);
            }

            if (NativeMethods.HasClassName(current, NativeMethods.ExplorerWindowClass))
                return (GetActiveTab(current), current);

            current = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
        }

        // Some views are hosted by a child window chain that GetAncestor cannot walk;
        // fall back to the root window and its active tab.
        var root = NativeMethods.GetAncestor(childHwnd, NativeMethods.GA_ROOT);
        if (NativeMethods.HasClassName(root, NativeMethods.ExplorerWindowClass))
            return (GetActiveTab(root), root);

        return (0, 0);
    }

    /// <summary>Tab -> location map from the shell, cached for a fraction of a second.</summary>
    public static Dictionary<nint, ShellTabLocation> GetLocationMap(int maxAgeMs = 400, bool refresh = false)
    {
        lock (CacheGate)
        {
            var now = Environment.TickCount64;
            if (!refresh && now - _cache.Tick <= maxAgeMs && _cache.Map.Count > 0)
                return _cache.Map;

            var map = ShellCom.QueryTabLocations();
            _cache = (now, map);
            return map;
        }
    }

    /// <summary>Invalidates the cached shell map (call after opening/closing tabs).</summary>
    public static void InvalidateCache()
    {
        lock (CacheGate)
            _cache = (0, new Dictionary<nint, ShellTabLocation>());
    }

    /// <summary>Full picture: every window with its tabs and locations.</summary>
    public static List<ExplorerWindow> Enumerate(bool useCache = true)
    {
        var map = GetLocationMap(refresh: !useCache);

        var result = new List<ExplorerWindow>();
        foreach (var windowHwnd in GetWindows())
        {
            var tabs = new List<ExplorerTab>();
            var tabHandles = GetTabs(windowHwnd);
            for (var i = 0; i < tabHandles.Count; i++)
            {
                var tabHwnd = tabHandles[i];
                var location = map.TryGetValue(tabHwnd, out var entry)
                    ? entry.Location
                    : string.Empty;
                var name = map.TryGetValue(tabHwnd, out var entry2) && !string.IsNullOrEmpty(entry2.DisplayName)
                    ? entry2.DisplayName
                    : PathNormalizer.ToDisplayName(location);

                tabs.Add(new ExplorerTab(tabHwnd, windowHwnd, location, name));
            }

            // Windows 10 / very old builds: the shell entry is per window, not per tab.
            if (tabs.Count == 1 && string.IsNullOrEmpty(tabs[0].Location) && map.TryGetValue(windowHwnd, out var windowEntry))
                tabs[0] = tabs[0] with { Location = windowEntry.Location, DisplayName = windowEntry.DisplayName };

            result.Add(new ExplorerWindow(windowHwnd, tabs));
        }

        return result;
    }

    /// <summary>Every tab of every window, flattened.</summary>
    public static List<ExplorerTab> EnumerateTabs(bool useCache = true) =>
        Enumerate(useCache).SelectMany(w => w.Tabs).ToList();

    /// <summary>The Explorer window that currently has focus, or 0.</summary>
    public static nint GetForegroundWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return 0;

        if (NativeMethods.HasClassName(foreground, NativeMethods.ExplorerWindowClass))
            return foreground;

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        return NativeMethods.HasClassName(root, NativeMethods.ExplorerWindowClass) ? root : 0;
    }

    /// <summary>Most recently used Explorer window (first in z-order), or 0.</summary>
    public static nint GetMostRecentWindow()
    {
        var windows = GetWindows();
        return windows.Count > 0 ? windows[0] : 0;
    }

    /// <summary>
    /// Finds a tab showing <paramref name="location"/>. When <paramref name="acrossAllWindows"/>
    /// is false only <paramref name="preferredWindow"/> (and its tabs) are searched.
    /// </summary>
    public static ExplorerTab? FindTab(string location, nint preferredWindow = 0, bool acrossAllWindows = true,
        nint excludeWindow = 0)
    {
        var normalized = PathNormalizer.Normalize(location);
        if (normalized.Length == 0)
            return null;

        var windows = Enumerate().Where(w => w.Hwnd != excludeWindow).ToList();
        IEnumerable<ExplorerWindow> candidates = windows;

        if (!acrossAllWindows)
        {
            var targetHwnd = preferredWindow != 0 ? preferredWindow : GetMostRecentWindow();
            candidates = windows.Where(w => w.Hwnd == targetHwnd);
        }
        else if (preferredWindow != 0)
        {
            // Prefer a match in the window the user is working in, then look everywhere.
            var local = FindIn(windows.Where(w => w.Hwnd == preferredWindow), normalized);
            if (local is not null)
                return local;
            candidates = windows.Where(w => w.Hwnd != preferredWindow);
        }

        return FindIn(candidates, normalized);
    }

    private static ExplorerTab? FindIn(IEnumerable<ExplorerWindow> windows, string normalized)
    {
        foreach (var window in windows)
        {
            foreach (var tab in window.Tabs)
            {
                if (tab.Location.Length == 0)
                    continue;

                if (string.Equals(tab.Location, normalized, StringComparison.OrdinalIgnoreCase))
                    return tab;
            }
        }

        // Second pass with PIDL comparison for paths that only differ textually
        // (short names, junctions, trailing separators).
        foreach (var window in windows)
        {
            foreach (var tab in window.Tabs)
            {
                if (tab.Location.Length == 0)
                    continue;

                if (ShellCom.LocationsMatch(tab.Location, normalized))
                    return tab;
            }
        }

        return null;
    }

    /// <summary>Best guess at the window that should receive a new tab.</summary>
    public static nint ChooseTargetWindow(AppSettings settings, nint preferredWindow = 0)
    {
        if (preferredWindow != 0 && NativeMethods.HasClassName(preferredWindow, NativeMethods.ExplorerWindowClass))
            return preferredWindow;

        var foreground = GetForegroundWindow();
        if (foreground != 0 && settings.TargetWindow != TargetWindowPolicy.LastUsedWindow)
            return foreground;

        return settings.TargetWindow switch
        {
            TargetWindowPolicy.LargestWindow => Enumerate()
                .OrderByDescending(w => w.Tabs.Count)
                .Select(w => w.Hwnd)
                .FirstOrDefault(),
            _ => GetMostRecentWindow()
        };
    }
}
