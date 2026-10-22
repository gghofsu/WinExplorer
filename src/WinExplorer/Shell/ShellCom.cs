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
    // One Shell.Application instance per shell (STA) thread. Creating one per query several
    // times a second is both slow and a good way to upset the shell's automation server.
    [ThreadStatic] private static object? _shell;

    /// <summary>The registered Shell.Application type (has the real member table).</summary>
    private static Type? ProgIdType;

    [ThreadStatic]
    private static (long Tick, Dictionary<nint, ShellTabLocation> Map) _memo;

    /// <summary>Why the last query came back empty (for the log, --diagnose and --probe).</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>How many windows the shell automation reported in the last query.</summary>
    public static int LastWindowCount { get; private set; }

    /// <summary>True once a Shell.Application object was created on any thread.</summary>
    public static bool ShellCreated { get; private set; }

    /// <summary>True when the shell automation itself works (it returned at least one entry).</summary>
    public static bool AutomationAnswered => LastWindowCount > 0;

    private static void Note(string message)
    {
        if (LastError.Length == 0)
            LastError = message;
    }

    private static void NoteException(string where, Exception ex) =>
        Note($"{where}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");

    private static object? Shell()
    {
        if (_shell is not null)
            return _shell;

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                Note("Shell.Application is not registered.");
                return null;
            }

            _shell = Activator.CreateInstance(type);
            if (_shell is null)
                Note("Shell.Application could not be created.");
            else
                ShellCreated = true;
        }
        catch (Exception ex)
        {
            NoteException("Shell.Application", ex);
            _shell = null;
        }

        return _shell;
    }

    private static void DropShell()
    {
        var shell = _shell;
        _shell = null;
        if (shell is not null && Marshal.IsComObject(shell))
        {
            try { Marshal.ReleaseComObject(shell); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Returns tab handle -> location for every Explorer tab the shell knows about.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// Two engines, tried in the order that actually works on current builds: the shell
    /// windows object (typed interface, one entry per window or per tab) and the late-bound
    /// <c>Shell.Application</c> automation object. On Windows 11 26xxx the latter answers
    /// every property with <c>DISP_E_MEMBERNOTFOUND</c>, so it cannot be the first choice.
    /// </remarks>
    public static Dictionary<nint, ShellTabLocation> QueryTabLocations(bool fresh = false)
    {
        // A consolidation pass asks for every tab in a row; enumerating once per frame is
        // enough and keeps the automation servers out of trouble.
        if (!fresh && _memo.Tick != 0 && Environment.TickCount64 - _memo.Tick < 250)
            return _memo.Map;

        LastError = string.Empty;
        LastWindowCount = 0;

        var result = ShellWindowsQuery.Query(out var shellWindowsError);
        if (result.Count > 0)
        {
            LastEngine = "ShellWindows";
        }
        else
        {
            LastEngine = "none";

            var late = QueryViaShellApplication();
            if (late.Count > 0)
            {
                result = late;
                LastEngine = "Shell.Application";
            }
            else if (shellWindowsError.Length > 0)
            {
                LastError = LastError.Length == 0 ? shellWindowsError : $"{LastError} / {shellWindowsError}";
            }
        }

        LastWindowCount = result.Count;
        _memo = (Environment.TickCount64, result);
        return result;
    }

    /// <summary>
    /// The late-bound <c>Shell.Application.Windows</c> route: one entry per tab on some
    /// Windows builds, one per window on others.
    /// </summary>
    private static Dictionary<nint, ShellTabLocation> QueryViaShellApplication()
    {
        var result = new Dictionary<nint, ShellTabLocation>();

        object? windows = null;
        try
        {
            var shell = Shell();
            if (shell is null)
                return result;

            windows = GetProperty(shell, "Windows");
            if (windows is null)
            {
                Note("Shell.Application.Windows returned nothing.");
                return result;
            }

            var count = ToInt(GetProperty(windows, "Count"));
            if (count == 0)
                Note("The shell reports no Explorer windows at all.");

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
                    result[hwnd] = new ShellTabLocation(hwnd, ParentWindowOf(hwnd),
                        PathNormalizer.Normalize(location), name);
                }
                catch (Exception ex)
                {
                    // Ignore a single broken/closed item and keep enumerating.
                    NoteException($"Shell.Windows.Item({i})", ex);
                }
                finally
                {
                    Release(window);
                }
            }
        }
        catch (Exception ex)
        {
            // Shell automation unavailable (rare, e.g. during Explorer restart).
            NoteException("Shell.Windows", ex);
            DropShell();
        }
        finally
        {
            Release(windows);
        }

        return result;
    }

    /// <summary>Every shell window item with its tab handle, for --probe.</summary>
    public static List<string> DescribeItems() => ShellWindowsQuery.Describe();

    /// <summary>Which automation engine produced the last answer (for --probe).</summary>
    public static string LastEngine { get; private set; } = "none";

    /// <summary>Forgets the memo (after navigating a tab).</summary>
    public static void InvalidateMemo() => _memo = (0, new Dictionary<nint, ShellTabLocation>());

    /// <summary>State of the shell automation, for --diagnose / --probe.</summary>
    public static string Describe()
    {
        var navigate = ShellWindowsQuery.LastNavigateError.Length == 0
            ? string.Empty
            : $", last navigation error {ShellWindowsQuery.LastNavigateError}";
        var shell = ShellCreated ? "created" : "not created";
        var answer = AutomationAnswered ? $"answered via {LastEngine}" : "no data";
        var error = LastError.Length == 0 ? "(none)" : LastError;
        var property = LastPropertyError.Length == 0 ? string.Empty : $", last failing call {LastPropertyError}";
        property += $", last navigation {ShellWindowsQuery.DescribeLastNavigation}";
        return $"shell automation: {shell}, {answer}, {LastWindowCount} window(s), last error {error}{property}{navigate}";
    }

    private static int _lookupsPerTab;
    private static int _lookupsPerWindow;
    private static int _lookupsUi;

    /// <summary>How tab folders were found so far (for --diagnose).</summary>
    public static string DescribeLookups() =>
        $"per-tab {Volatile.Read(ref _lookupsPerTab)}, per-window {Volatile.Read(ref _lookupsPerWindow)}, " +
        $"from the UI {Volatile.Read(ref _lookupsUi)}";

    /// <summary>Location of a single tab; empty when the shell does not know it yet.</summary>
    public static string? GetTabLocation(nint tabHwnd)
    {
        if (tabHwnd == 0)
            return null;

        var map = QueryTabLocations();

        // Per-tab builds (Windows 11 with tabs): the shell knows every tab.
        if (map.TryGetValue(tabHwnd, out var entry) && !string.IsNullOrEmpty(entry.Location))
        {
            Interlocked.Increment(ref _lookupsPerTab);
            return entry.Location;
        }

        // Per-window builds: the shell knows the window, and its entry describes the folder
        // that is showing. That is the answer for this tab when it is the active one (or the
        // only one) - background tabs are resolved by activating them, see
        // TabController.GetTabLocationsAsync.
        var window = ParentWindowOf(tabHwnd);
        var tabsOfWindow = window == 0 ? new List<nint>() : ExplorerWindows.GetTabs(window);
        var isActiveTab = window != 0 && ExplorerWindows.GetActiveTab(window) == tabHwnd;

        if (window != 0 && (tabsOfWindow.Count <= 1 || isActiveTab))
        {
            if (map.TryGetValue(window, out var windowEntry) && !string.IsNullOrEmpty(windowEntry.Location))
            {
                Interlocked.Increment(ref _lookupsPerWindow);
                return windowEntry.Location;
            }

            foreach (var candidate in map.Values)
            {
                if (candidate.WindowHwnd == window && !string.IsNullOrEmpty(candidate.Location))
                {
                    Interlocked.Increment(ref _lookupsPerWindow);
                    return candidate.Location;
                }
            }
        }

        // The automation server did not know this tab; ask the UI instead, best handle first.
        var fromUi = AddressBarText.TryRead(tabHwnd)
                     ?? UiaAddressBar.TryRead(tabHwnd)
                     ?? (window != 0 ? AddressBarText.TryRead(window) ?? UiaAddressBar.TryRead(window) : null);

        if (fromUi is not null)
            Interlocked.Increment(ref _lookupsUi);

        return fromUi;
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

        // Entries are keyed by tab on some builds and by window on others; accept both.
        foreach (var candidate in map.Values)
        {
            if (string.IsNullOrEmpty(candidate.Location))
                continue;

            if (candidate.WindowHwnd == windowHwnd || candidate.TabHwnd == windowHwnd ||
                ParentWindowOf(candidate.TabHwnd) == windowHwnd)
            {
                return candidate.Location;
            }
        }

        // Second opinion: the active tab's own address bar, then the window's.
        if (activeTab != 0 && AddressBarText.TryRead(activeTab) is { Length: > 0 } fromTab)
            return fromTab;

        if (AddressBarText.TryRead(windowHwnd) is { Length: > 0 } fromWindow)
            return fromWindow;

        // Third opinion: UI Automation of the same window.
        if (activeTab != 0 && UiaAddressBar.TryRead(activeTab) is { Length: > 0 } fromTabUia)
            return fromTabUia;

        return UiaAddressBar.TryRead(windowHwnd);
    }

    /// <summary>
    /// Asks the shell to show <paramref name="location"/> in the given tab. Fire and
    /// forget: use <see cref="WaitForLocation"/> to confirm the navigation landed.
    /// </summary>
    public static bool NavigateTab(nint tabHwnd, string location)
    {
        if (tabHwnd == 0 || string.IsNullOrWhiteSpace(location))
            return false;

        // The shell windows object is the engine that works on current builds: it navigates
        // the item that owns this tab, so no tab has to be activated first.
        if (ShellWindowsQuery.Navigate(tabHwnd, location))
        {
            InvalidateMemo();
            return true;
        }

        if (NavigateOnce(tabHwnd, location))
        {
            InvalidateMemo();
            return true;
        }

        // A tab this fresh may not be registered with the shell yet.
        Thread.Sleep(150);

        if (ShellWindowsQuery.Navigate(tabHwnd, location))
        {
            InvalidateMemo();
            return true;
        }

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
        object? windows = null;
        try
        {
            var shell = Shell();
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
        catch (Exception ex)
        {
            NoteException("Navigate2", ex);
            DropShell();
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows))
            {
                try { Marshal.ReleaseComObject(windows); } catch { /* ignore */ }
            }
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
            // A runtime callable wrapper's GetType() is System.__ComObject, which has no
            // members at all - asking it for "Windows" fails with DISP_E_MEMBERNOTFOUND.
            // The ProgID type carries the real member table.
            var type = instance.GetType();
            if (type == typeof(object) || type.Name.Contains("__ComObject", StringComparison.Ordinal))
                type = ProgIdType ??= Type.GetTypeFromProgID("Shell.Application");

            if (type is null)
                return null;

            return type.InvokeMember(property, System.Reflection.BindingFlags.GetProperty,
                null, instance, null);
        }
        catch (Exception ex)
        {
            // Late binding hides the reason; keep it, it is the only clue there is.
            var actual = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? inner
                : ex;

            NoteException(property, actual);
            LastPropertyError = $"{property}: 0x{actual.HResult:X8} {actual.Message}";
            return null;
        }
    }

    /// <summary>The last property/method that failed on the automation object (for --probe).</summary>
    public static string LastPropertyError { get; private set; } = string.Empty;

    private static object? Invoke(object? instance, string method, params object?[] args)
    {
        if (instance is null)
            return null;

        try
        {
            return instance.GetType().InvokeMember(method, System.Reflection.BindingFlags.InvokeMethod,
                null, instance, args);
        }
        catch (Exception ex)
        {
            if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner })
                NoteException(method, inner);
            else
                NoteException(method, ex);

            return null;
        }
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;

        try { Marshal.ReleaseComObject(instance); } catch { /* ignore */ }
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
