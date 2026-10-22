using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Reads and drives the Explorer windows/tabs through <c>CLSID_ShellWindows</c> - the object
/// <c>Shell.Application.Windows</c> wraps, created and called directly.
/// </summary>
/// <remarks>
/// One shell window item does <b>not</b> always equal one tab: on current builds every item
/// reports the frame window through <c>HWND</c> and only the folder of the tab that is
/// showing. The tab an item belongs to has to be asked of the item itself
/// (<c>IServiceProvider.QueryService(IShellBrowser)</c> → <c>IShellBrowser.GetWindow</c>).
/// With that handle, <c>Navigate2</c> on the item navigates exactly that tab - without
/// activating it, and therefore without flickering.
/// </remarks>
internal static class ShellWindowsQuery
{
    /// <summary>One shell window item: the tab it owns, its window, and what it shows.</summary>
    internal sealed record ShellItem(object Window, nint TabHwnd, nint WindowHwnd, string Location);

    /// <summary>The top level browser service Explorer registers (SID_STopLevelBrowser).</summary>
    private static readonly Guid SID_StopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>Why the last navigation attempt failed (for logs and --probe).</summary>
    public static string LastNavigateError { get; private set; } = string.Empty;

    /// <summary>The top level browser service Explorer registers (SID_STopLevelBrowser).</summary>
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>Enumerates every shell window item with the tab handle it belongs to.</summary>
    internal static List<ShellItem> Enumerate(out string error, out int itemCount)
    {
        var items = new List<ShellItem>();
        error = string.Empty;
        itemCount = 0;

        object? raw = null;
        try
        {
            raw = new ShellWindowsClass();
            var typed = raw as IShellWindows;
            var count = typed is not null
                ? (typed.get_Count(out var c) == 0 ? c : 0)
                : ToInt(Read(raw, "Count", out _));

            itemCount = count;
            if (count == 0)
                error = "ShellWindows reports no window at all";

            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    string? itemError = null;
                    if (typed is not null)
                    {
                        var hr = typed.Item(i, out window);
                        if (hr != 0)
                            itemError = $"0x{hr:X8}";
                    }
                    else
                    {
                        window = Read(raw, "Item", out itemError, i);
                    }

                    if (window is null)
                    {
                        if (error.Length == 0)
                            error = $"ShellWindows.Item({i}) returned nothing ({itemError ?? "no error"})";
                        continue;
                    }

                    var reportedHandle = (nint)ToLong(Read(window, "HWND", out _));
                    var tabHwnd = TabHandleOf(window);
                    var windowHwnd = ResolveWindow(reportedHandle, tabHwnd);

                    if (windowHwnd == 0 && tabHwnd == 0)
                    {
                        if (error.Length == 0)
                            error = $"ShellWindows.Item({i}) has no handle";
                        continue;
                    }

                    var location = ReadLocation(window);
                    items.Add(new ShellItem(window, tabHwnd, windowHwnd, PathNormalizer.Normalize(location)));
                }
                catch (Exception ex)
                {
                    if (error.Length == 0)
                        error = $"ShellWindows.Item({i}): {Describe(ex)}";
                }
            }
        }
        catch (Exception ex)
        {
            error = $"CLSID_ShellWindows: {Describe(ex)}";
        }
        finally
        {
            // The items stay alive inside the list; only the collection is released.
            Release(raw);
        }

        return items;
    }

    /// <summary>Tab handle -> location, exactly like <see cref="ShellCom.QueryTabLocations"/>.</summary>
    public static Dictionary<nint, ShellTabLocation> Query(out string error)
    {
        var result = new Dictionary<nint, ShellTabLocation>();
        var items = Enumerate(out error, out _);

        try
        {
            foreach (var item in items)
            {
                var key = item.TabHwnd != 0 ? item.TabHwnd : item.WindowHwnd;
                if (key == 0)
                    continue;

                var name = PathNormalizer.ToDisplayName(item.Location);
                result[key] = new ShellTabLocation(key, item.WindowHwnd, item.Location, name);
            }
        }
        finally
        {
            foreach (var item in items)
                Release(item.Window);
        }

        return result;
    }

    /// <summary>
    /// Shows <paramref name="location"/> in <paramref name="tabHwnd"/> by navigating the shell
    /// window item that owns it. No tab switching is involved.
    /// </summary>
    public static bool Navigate(nint tabHwnd, string location)
    {
        if (tabHwnd == 0 || string.IsNullOrWhiteSpace(location))
            return false;

        LastNavigateError = string.Empty;

        var items = Enumerate(out var error, out _);
        try
        {
            var match = items.FirstOrDefault(i => i.TabHwnd == tabHwnd);

            // Builds that only report the frame window: usable when the window has one tab.
            match ??= items.FirstOrDefault(i =>
                i.TabHwnd == 0 && i.WindowHwnd == ParentWindowOf(tabHwnd) &&
                ExplorerWindows.GetTabs(i.WindowHwnd).Count <= 1);

            if (match is null)
            {
                LastNavigateError = $"no shell window item owns tab 0x{tabHwnd:X}" +
                                    (error.Length == 0 ? string.Empty : $" ({error})");
                return false;
            }

            var hr = Invoke(match.Window, "Navigate2", location);
            if (hr != 0)
            {
                LastNavigateError = $"Navigate2 0x{hr:X8} for tab 0x{tabHwnd:X}";
                return false;
            }

            _lastNavigatedTab = tabHwnd;
            _lastNavigatedLocation = PathNormalizer.Normalize(location);
            return true;
        }
        finally
        {
            foreach (var item in items)
                Release(item.Window);
        }
    }

    private static nint _lastNavigatedTab;
    private static string _lastNavigatedLocation = string.Empty;

    /// <summary>The tab Navigate2 was last called on (for diagnostics).</summary>
    public static string DescribeLastNavigation =>
        _lastNavigatedTab == 0 ? "(none)" : $"tab 0x{_lastNavigatedTab:X} -> '{_lastNavigatedLocation}'";

    /// <summary>The tab handle a shell window item belongs to, or 0 when it cannot be asked.</summary>
    private static nint TabHandleOf(object window)
    {
        IShellBrowser? browser = null;
        try
        {
            if (window is not IServiceProviderNative provider)
                return 0;

            var service = SID_StopLevelBrowserSafe();
            var iid = typeof(IShellBrowser).GUID;

            var hr = provider.QueryService(ref service, ref iid, out browser);
            if ((hr != 0 || browser is null) && !service.Equals(iid))
            {
                // Some builds only answer with the interface id as the service id.
                var retry = iid;
                hr = provider.QueryService(ref retry, ref iid, out browser);
            }

            if (hr != 0 || browser is null)
                return 0;

            return browser.GetWindow(out var hwnd) == 0 ? hwnd : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (browser is not null && Marshal.IsComObject(browser))
            {
                try { Marshal.ReleaseComObject(browser); } catch { /* ignore */ }
            }
        }
    }

    private static Guid SID_StopLevelBrowserSafe() => SID_StopLevelBrowser;

    /// <summary>Reads one item's properties directly, for --probe.</summary>
    public static string ProbePropertyErrors(nint windowHwnd)
    {
        var items = Enumerate(out var error, out var count);

        try
        {
            foreach (var item in items)
            {
                var matches = item.WindowHwnd == windowHwnd || item.TabHwnd == windowHwnd ||
                              (item.TabHwnd != 0 && ParentWindowOf(item.TabHwnd) == windowHwnd) ||
                              (item.WindowHwnd != 0 && ParentWindowOf(windowHwnd) == item.WindowHwnd);

                if (!matches)
                    continue;

                var url = Read(item.Window, "LocationURL", out var urlError);
                var owner = item.TabHwnd == 0
                    ? "no tab handle"
                    : $"tab 0x{item.TabHwnd:X}";

                return url is string text && text.Length > 0
                    ? $"LocationURL = {text} ({owner})"
                    : $"LocationURL is empty ({owner}{(urlError is null ? string.Empty : ", " + urlError)})";
            }

            return $"window not found among {count} item(s)" +
                   (error.Length == 0 ? string.Empty : $": {error}");
        }
        finally
        {
            foreach (var item in items)
                Release(item.Window);
        }
    }

    /// <summary>Every item as a line, for --probe.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        var items = Enumerate(out var error, out var count);

        lines.Add($"{count} item(s)" + (error.Length == 0 ? string.Empty : $", {error}"));
        foreach (var item in items)
        {
            lines.Add($"    item: tab 0x{item.TabHwnd:X}, window 0x{item.WindowHwnd:X}, '{item.Location}'");
            Release(item.Window);
        }

        return lines;
    }

    private static string Describe(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
            return $"{inner.GetType().Name} 0x{inner.HResult:X8} {inner.Message}";

        return $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
    }

    private static object? Read(object? instance, string property, out string? error, params object?[] args)
    {
        error = null;
        if (instance is null)
            return null;

        try
        {
            var flags = args.Length == 0 ? BindingFlags.GetProperty : BindingFlags.InvokeMethod;
            return instance.GetType().InvokeMember(property, flags, null, instance, args.Length == 0 ? null : args);
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            error = $"0x{actual.HResult:X8} {actual.Message}";
            return null;
        }
    }

    private static object? Read(object? instance, string property) => Read(instance, property, out _);

    /// <summary>Calls a method; returns the HRESULT (0 on success).</summary>
    private static int Invoke(object instance, string method, params object?[] args)
    {
        try
        {
            instance.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, instance, args);
            return 0;
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return actual.HResult;
        }
    }

    private static string ReadLocation(object window)
    {
        if (Read(window, "LocationURL") is string url && !string.IsNullOrWhiteSpace(url))
            return PathNormalizer.Normalize(url);

        // Virtual folders only expose their PIDL backed path.
        try
        {
            var document = Read(window, "Document");
            var folder = document is null ? null : Read(document, "Folder");
            var self = folder is null ? null : Read(folder, "Self");
            if (self is not null && Read(self, "Path") is string path && !string.IsNullOrWhiteSpace(path))
                return PathNormalizer.Normalize(path);
        }
        catch
        {
            // Not a ShellFolderView.
        }

        return string.Empty;
    }

    /// <summary>Works out the frame window from what an item reports.</summary>
    private static nint ResolveWindow(nint reportedHandle, nint tabHwnd)
    {
        if (reportedHandle != 0 && NativeMethods.HasClassName(reportedHandle, NativeMethods.ExplorerWindowClass))
            return reportedHandle;

        var fromTab = ParentWindowOf(tabHwnd);
        if (fromTab != 0)
            return fromTab;

        if (reportedHandle != 0)
            return ParentWindowOf(reportedHandle);

        return 0;
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
                int i => i,
                long l => l,
                nint n => (long)n,
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The frame window that owns <paramref name="hwnd"/> (or 0).</summary>
    internal static nint ParentWindowOf(nint hwnd)
    {
        if (hwnd == 0)
            return 0;

        if (NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
            return hwnd;

        var current = hwnd;
        for (var depth = 0; depth < 8 && current != 0; depth++)
        {
            current = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
            if (NativeMethods.HasClassName(current, NativeMethods.ExplorerWindowClass))
                return current;
        }

        return 0;
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;

        try { Marshal.ReleaseComObject(instance); } catch { /* ignore */ }
    }
}
