using System.Globalization;
using System.Runtime.InteropServices;
using WinExplorer.Core;

namespace WinExplorer.Shell;

/// <summary>One tab as reported by the shell.</summary>
internal sealed record ShellTabLocation(nint TabHwnd, nint WindowHwnd, string Location, string DisplayName);

/// <summary>
/// Thin wrapper over the shell's automation object (<c>Shell.Application</c>).
/// </summary>
/// <remarks>
/// Explorer exposes one <c>ShellWindows</c> entry per *tab* on Windows 11 (its
/// <c>HWND</c> is the tab's <c>ShellTabWindowClass</c> window) and one entry per window
/// on older builds. Everything here must run on an STA thread; use <c>StaExecutor</c>.
/// </remarks>
internal static class ShellCom
{
    /// <summary>
    /// Returns tab handle -> location for every Explorer tab the shell knows about.
    /// Never throws.
    /// </summary>
    public static Dictionary<nint, ShellTabLocation> QueryTabLocations()
    {
        var result = new Dictionary<nint, ShellTabLocation>();
        object? shell = null;
        object? windows = null;

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return result;

            shell = Activator.CreateInstance(type);
            if (shell is null)
                return result;

            windows = GetProperty(shell, "Windows");
            if (windows is null)
                return result;

            var count = ToInt(GetProperty(windows, "Count"));
            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = Invoke(windows, "Item", i);
                    if (window is null)
                        continue;

                    var hwnd = (nint)ToLong(GetProperty(window, "HWND"));
                    if (hwnd == 0)
                        continue;

                    var location = ReadLocation(window);
                    var name = ReadString(window, "LocationName") ?? PathNormalizer.ToDisplayName(location);

                    // The shell may hand us the same tab twice while it is being created.
                    result[hwnd] = new ShellTabLocation(hwnd, ParentWindowOf(hwnd), PathNormalizer.Normalize(location), name);
                }
                catch
                {
                    // Ignore a single broken/closed item and keep enumerating.
                }
                finally
                {
                    if (window is not null && Marshal.IsComObject(window))
                        Marshal.ReleaseComObject(window);
                }
            }
        }
        catch
        {
            // Shell automation unavailable (rare, e.g. during Explorer restart).
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows))
                Marshal.ReleaseComObject(windows);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return result;
    }

    /// <summary>Location of a single tab; empty when the shell does not know it yet.</summary>
    public static string? GetTabLocation(nint tabHwnd)
    {
        if (tabHwnd == 0)
            return null;

        var map = QueryTabLocations();
        return map.TryGetValue(tabHwnd, out var entry) ? entry.Location : null;
    }

    /// <summary>Location of the tab that currently has focus in <paramref name="windowHwnd"/>.</summary>
    public static string? GetActiveTabLocation(nint windowHwnd)
    {
        var map = QueryTabLocations();

        var activeTab = ExplorerWindows.GetActiveTab(windowHwnd);
        if (activeTab != 0 && map.TryGetValue(activeTab, out var entry) && !string.IsNullOrEmpty(entry.Location))
            return entry.Location;

        // Windows 10 style: the shell entry belongs to the window itself.
        if (map.TryGetValue(windowHwnd, out var direct))
            return direct.Location;

        foreach (var candidate in map.Values)
        {
            if (candidate.WindowHwnd == windowHwnd)
                return candidate.Location;
        }

        return null;
    }

    /// <summary>
    /// Asks the shell to show <paramref name="location"/> in the given tab. Fire and
    /// forget: use <see cref="WaitForLocation"/> to confirm the navigation landed.
    /// </summary>
    public static bool NavigateTab(nint tabHwnd, string location)
    {
        if (tabHwnd == 0 || string.IsNullOrWhiteSpace(location))
            return false;

        if (NavigateOnce(tabHwnd, location))
            return true;

        // A tab this fresh may not be registered with the shell yet.
        Thread.Sleep(150);
        return NavigateOnce(tabHwnd, location);
    }

    /// <summary>
    /// Polls until the tab reports <paramref name="expected"/> (or any location, when the
    /// shell canonicalizes it). Returns false only when the tab stays unknown/empty.
    /// </summary>
    public static bool WaitForLocation(nint tabHwnd, string expected, int timeoutMs = 3000)
    {
        var normalized = PathNormalizer.Normalize(expected);
        var deadline = Environment.TickCount64 + timeoutMs;
        string? last = null;

        while (Environment.TickCount64 < deadline)
        {
            var current = GetTabLocation(tabHwnd);
            last = current;
            if (!string.IsNullOrEmpty(current) &&
                (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase) ||
                 LocationsMatch(current, normalized)))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        // A different but valid location means the navigation worked (short names,
        // redirected folders, ...). Only an empty location is a failure.
        return !string.IsNullOrWhiteSpace(last);
    }

    private static bool NavigateOnce(nint tabHwnd, string location)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return false;

            shell = Activator.CreateInstance(type);
            if (shell is null)
                return false;

            windows = GetProperty(shell, "Windows");
            if (windows is null)
                return false;

            var count = ToInt(GetProperty(windows, "Count"));
            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = Invoke(windows, "Item", i);
                    if (window is null)
                        continue;

                    var hwnd = (nint)ToLong(GetProperty(window, "HWND"));

                    // Match either the tab itself (Win11) or its window (Win10).
                    if (hwnd != tabHwnd && ExplorerWindows.GetActiveTab(hwnd) != tabHwnd)
                        continue;

                    Invoke(window, "Navigate2", location);
                    return true;
                }
                catch
                {
                    // try the next entry
                }
                finally
                {
                    if (window is not null && Marshal.IsComObject(window))
                        Marshal.ReleaseComObject(window);
                }
            }
        }
        catch
        {
            // fall through
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows))
                Marshal.ReleaseComObject(windows);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return false;
    }

    /// <summary>True when two normalized locations point at the same place.</summary>
    public static bool LocationsMatch(string? a, string? b)
    {
        var left = PathNormalizer.Normalize(a);
        var right = PathNormalizer.Normalize(b);
        if (left.Length == 0 || right.Length == 0)
            return false;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        // PIDL comparison resolves 8.3 names, junctions and virtual folders.
        try
        {
            using var comparer = new ShellPathComparer();
            return comparer.IsEquivalent(left, right);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Name of the folder shown in a tab, for UI and logging.</summary>
    public static string DisplayNameOf(string? location) => PathNormalizer.ToDisplayName(location);

    private static string ReadLocation(object window)
    {
        var url = ReadString(window, "LocationURL");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var normalized = PathNormalizer.Normalize(url);
            if (normalized.Length > 0)
                return normalized;
        }

        // Virtual folders (This PC, Recycle Bin) only expose their PIDL backed path.
        try
        {
            var document = GetProperty(window, "Document");
            var folder = document is null ? null : GetProperty(document, "Folder");
            var self = folder is null ? null : GetProperty(folder, "Self");
            var path = self is null ? null : GetProperty(self, "Path") as string;
            if (!string.IsNullOrWhiteSpace(path))
                return PathNormalizer.Normalize(path);
        }
        catch
        {
            // Not a ShellFolderView (e.g. a web page); fall back to the URL above.
        }

        return string.Empty;
    }

    private static string? ReadString(object instance, string property) =>
        GetProperty(instance, property) as string;

    private static object? GetProperty(object? instance, string property)
    {
        if (instance is null)
            return null;

        try
        {
            return instance.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty,
                null, instance, null);
        }
        catch
        {
            return null;
        }
    }

    private static object? Invoke(object? instance, string method, params object?[] args)
    {
        if (instance is null)
            return null;

        try
        {
            return instance.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod,
                null, instance, args);
        }
        catch
        {
            return null;
        }
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static long ToLong(object? value)
    {
        try
        {
            return value switch
            {
                null => 0,
                nint n => (long)n,
                int i => i,
                long l => l,
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static nint ParentWindowOf(nint hwnd)
    {
        if (WinExplorer.Interop.NativeMethods.HasClassName(hwnd, WinExplorer.Interop.NativeMethods.ExplorerWindowClass))
            return hwnd;

        var parent = WinExplorer.Interop.NativeMethods.GetAncestor(hwnd, WinExplorer.Interop.NativeMethods.GA_PARENT);
        return WinExplorer.Interop.NativeMethods.HasClassName(parent, WinExplorer.Interop.NativeMethods.ExplorerWindowClass)
            ? parent
            : 0;
    }
}
