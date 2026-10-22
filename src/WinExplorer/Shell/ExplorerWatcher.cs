using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;
using WinExplorer.Services;

namespace WinExplorer.Shell;

/// <summary>
/// The engine. A port of w4po/ExplorerTabUtility's <c>ExplorerWatcher</c>: File Explorer
/// tabs are tracked through the shell's <c>ShellWindows</c> collection and its
/// <c>WindowRegistered</c> event, new windows are hidden and reopened as tabs of the one
/// main window, and a folder that is already open anywhere gets its existing tab selected
/// instead of a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// Everything COM lives on one pumping STA thread (<see cref="StaExecutor"/>): the
/// <c>ShellWindows</c> object, the event sinks and the <see cref="ShellPathComparer"/>.
/// The decision logic runs on worker threads and funnels COM work back to that thread.
/// </para>
/// <para>
/// On Windows 11 every Explorer <i>tab</i> is one <see cref="IWebBrowser2"/> entry of the
/// collection: <c>HWND</c> is the frame (<c>CabinetWClass</c>) and
/// <c>IShellBrowser.GetWindow</c> answers the tab (<c>ShellTabWindowClass</c>). Tab
/// creation and selection use Explorer's own accelerator commands
/// (<c>0xA21B</c> = Ctrl+T, <c>0xA221</c> = Ctrl+1..9, <c>0xA021</c> = Ctrl+W).
/// </para>
/// </remarks>
internal sealed class ExplorerWatcher : IDisposable
{
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>One tracked Explorer tab.</summary>
    private sealed class TabEntry
    {
        public required IWebBrowser2 Browser;
        public nint TabHandle;
        public nint FrameHandle;
        public long CreatedAt = Environment.TickCount64;
        public string Location = string.Empty;
        public string Name = string.Empty;
        public ComEventSubscription? Events;
        public WebBrowserEventsSink? Sink;

        /// <summary>True while the window is being folded into a tab (never a dedupe target).</summary>
        public bool Folding;
    }

    private readonly Logger _log;
    private readonly SettingsStore _settings;
    private readonly TabHistory _history;
    private readonly StaExecutor _sta;

    private readonly List<TabEntry> _entries = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _openLock = new(1, 1);

    private object? _shellWindows;
    private ShellWindowsEventsSink? _shellWindowsSink;
    private ComEventSubscription? _shellWindowsEvents;
    private ShellPathComparer? _comparer;

    private NativeMethods.WinEventProc? _showCallback;
    private nint _showHook;

    private readonly Dictionary<nint, long> _hiddenWindows = new();
    private System.Threading.Timer? _healthTimer;
    private int _healthRunning;

    private nint _mainWindow;
    private long _startedAt;
    private bool _started;
    private bool _disposed;

    /// <summary>Keep hands off brand new windows for this long after start (session restore).</summary>
    private const int StartupGraceMs = 5000;

    public ExplorerWatcher(Logger log, SettingsStore settings, TabHistory history)
    {
        _log = log;
        _settings = settings;
        _history = history;
        _sta = new StaExecutor("WinExplorer.Shell", log: log);
    }

    /// <summary>Human readable one-liners for the activity list.</summary>
    public event EventHandler<string>? Activity;

    public bool IsRunning => _started;

    private AppSettings Settings => _settings.Current;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public async Task StartAsync()
    {
        if (_started || _disposed)
            return;
        _started = true;
        _startedAt = Environment.TickCount64;

        await _sta.RunAsync(InitializeShellObjects).ConfigureAwait(false);

        _healthTimer = new System.Threading.Timer(_ => HealthCheck(), null, 5000, 5000);
        _log.Info("Explorer watcher started.");
    }

    public void Stop()
    {
        if (!_started)
            return;
        _started = false;

        _healthTimer?.Dispose();
        _healthTimer = null;

        try
        {
            _sta.RunAsync(DisposeShellObjects).Wait(3000);
        }
        catch
        {
            // Shutting down; Explorer may already be gone.
        }

        RevealHiddenWindows(onlyExpired: false);
        _log.Info("Explorer watcher stopped.");
    }

    /// <summary>Creates the shell objects and hooks. Runs on the STA thread.</summary>
    private void InitializeShellObjects()
    {
        _comparer = new ShellPathComparer();

        _shellWindows = new ShellWindowsClass();

        // Track everything that is already open. Windows that exist before WinExplorer
        // starts are tracked (so dedupe sees them) but never folded away behind the
        // user's back; the explicit "consolidate" action is for that.
        ForEachShellWindow(browser =>
        {
            MarkSeen(browser);
            AddEntry(browser);
        });

        _shellWindowsSink = new ShellWindowsEventsSink(OnWindowRegistered, OnWindowRevoked);
        _shellWindowsEvents = ComEventSubscription.TryAdvise(
            _shellWindows, typeof(DShellWindowsEvents).GUID, _shellWindowsSink);

        if (_shellWindowsEvents is null)
            _log.Warn("Could not subscribe to the shell's WindowRegistered event.");

        // Hide brand new Explorer windows before they are painted; the registered handler
        // decides right after whether they become a tab or get shown again.
        _showCallback = OnWindowShown;
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            0, _showCallback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>Releases the shell objects. Runs on the STA thread.</summary>
    private void DisposeShellObjects()
    {
        if (_showHook != 0)
        {
            NativeMethods.UnhookWinEvent(_showHook);
            _showHook = 0;
        }
        _showCallback = null;

        _shellWindowsEvents?.Dispose();
        _shellWindowsEvents = null;
        _shellWindowsSink = null;

        List<TabEntry> entries;
        lock (_gate)
        {
            entries = _entries.ToList();
            _entries.Clear();
        }

        foreach (var entry in entries)
            ReleaseEntry(entry);

        if (_shellWindows is not null)
        {
            try { Marshal.ReleaseComObject(_shellWindows); }
            catch { /* ignore */ }
            _shellWindows = null;
        }

        _comparer?.Dispose();
        _comparer = null;
    }

    // ------------------------------------------------------------------
    // Tracking tabs (ShellWindows bookkeeping)
    // ------------------------------------------------------------------

    /// <summary>Enumerates the ShellWindows items. Runs on the STA thread.</summary>
    private void ForEachShellWindow(Action<IWebBrowser2> action)
    {
        if (_shellWindows is not IShellWindows typed)
            return;

        if (typed.get_Count(out var count) != 0)
            return;

        for (var i = 0; i < count; i++)
        {
            try
            {
                if (typed.Item(i, out var item) != 0 || item is not IWebBrowser2 browser)
                    continue;

                action(browser);
            }
            catch
            {
                // A window can disappear while we enumerate.
            }
        }
    }

    private static bool WasSeen(IWebBrowser2 browser)
    {
        try { return browser.GetProperty("WinExplorerSeen") is not null; }
        catch { return true; }
    }

    private static void MarkSeen(IWebBrowser2 browser)
    {
        try { browser.PutProperty("WinExplorerSeen", true); }
        catch { /* ignore */ }
    }

    /// <summary>Starts tracking one shell window (tab). Runs on the STA thread.</summary>
    private TabEntry? AddEntry(IWebBrowser2 browser)
    {
        var entry = new TabEntry { Browser = browser };

        try
        {
            entry.FrameHandle = (nint)browser.HWND;
            entry.TabHandle = GetTabHandle(browser);
            entry.Location = ReadLocation(browser);
            entry.Name = ReadName(browser);
        }
        catch
        {
            // The window closed while we were looking at it.
            return null;
        }

        entry.Sink = new WebBrowserEventsSink(
            navigateComplete: () => OnEntryNavigated(entry),
            quit: () => OnEntryQuit(entry));
        entry.Events = ComEventSubscription.TryAdvise(browser, typeof(DWebBrowserEvents2).GUID, entry.Sink);

        lock (_gate)
        {
            if (_entries.Any(e => ReferenceEquals(e.Browser, browser) ||
                                  (entry.TabHandle != 0 && e.TabHandle == entry.TabHandle)))
            {
                // Already tracked (double registration).
                entry.Events?.Dispose();
                return _entries.First(e => ReferenceEquals(e.Browser, browser) ||
                                           (entry.TabHandle != 0 && e.TabHandle == entry.TabHandle));
            }

            _entries.Add(entry);

            if (_entries.Count == 1 && entry.FrameHandle != 0)
                _mainWindow = entry.FrameHandle;
        }

        return entry;
    }

    /// <summary>The ShellTabWindowClass handle a shell window belongs to. STA thread.</summary>
    private static nint GetTabHandle(IWebBrowser2 browser)
    {
        try
        {
            if (browser is not IServiceProviderNative provider)
                return 0;

            var service = SID_STopLevelBrowser;
            var iid = typeof(Interop.IShellBrowser).GUID;
            if (provider.QueryService(ref service, ref iid, out var shellBrowser) != 0 || shellBrowser is null)
                return 0;

            try
            {
                return shellBrowser.GetWindow(out var hwnd) == 0 ? hwnd : 0;
            }
            finally
            {
                Marshal.ReleaseComObject(shellBrowser);
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The folder a shell window shows, normalized. STA thread.</summary>
    private static string ReadLocation(IWebBrowser2 browser)
    {
        try
        {
            var url = browser.LocationURL;
            if (!string.IsNullOrWhiteSpace(url))
                return PathNormalizer.Normalize(url);
        }
        catch
        {
            // fall through
        }

        // Virtual folders (This PC, Recycle Bin, ...) report no URL; ask the folder itself:
        // Document.Folder.Self.Path - late bound, the Document object has no stable IID.
        try
        {
            var document = browser.Document;
            if (document is null)
                return string.Empty;

            try
            {
                var folder = document.GetType().InvokeMember("Folder",
                    System.Reflection.BindingFlags.GetProperty, null, document, null);
                if (folder is null)
                    return string.Empty;

                var self = folder.GetType().InvokeMember("Self",
                    System.Reflection.BindingFlags.GetProperty, null, folder, null);
                if (self is null)
                    return string.Empty;

                var path = self.GetType().InvokeMember("Path",
                    System.Reflection.BindingFlags.GetProperty, null, self, null) as string;

                return PathNormalizer.Normalize(path);
            }
            finally
            {
                if (Marshal.IsComObject(document))
                    Marshal.ReleaseComObject(document);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadName(IWebBrowser2 browser)
    {
        try { return browser.LocationName ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>NavigateComplete2: keep the entry's location fresh. STA thread.</summary>
    private void OnEntryNavigated(TabEntry entry)
    {
        try
        {
            entry.Location = ReadLocation(entry.Browser);
            entry.Name = ReadName(entry.Browser);

            if (entry.TabHandle == 0)
                entry.TabHandle = GetTabHandle(entry.Browser);
        }
        catch
        {
            // The tab may be mid-close.
        }
    }

    /// <summary>OnQuit: the tab (or its whole window) closed. STA thread.</summary>
    private void OnEntryQuit(TabEntry entry)
    {
        lock (_gate)
            _entries.Remove(entry);

        if (!entry.Folding && entry.Location.Length > 0)
            _history.RecordClosed(entry.Location);

        ReleaseEntry(entry);
    }

    private static void ReleaseEntry(TabEntry entry)
    {
        try { entry.Events?.Dispose(); }
        catch { /* ignore */ }
        entry.Events = null;

        try
        {
            if (Marshal.IsComObject(entry.Browser))
                Marshal.ReleaseComObject(entry.Browser);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Entries whose COM object died without an OnQuit (Explorer crash).</summary>
    private void PruneDeadEntries()
    {
        List<TabEntry> snapshot;
        lock (_gate)
            snapshot = _entries.ToList();

        foreach (var entry in snapshot)
        {
            var alive = entry.TabHandle != 0 && NativeMethods.IsWindow(entry.TabHandle);
            if (!alive)
            {
                try
                {
                    _ = entry.Browser.HWND;
                    alive = true;
                }
                catch
                {
                    alive = false;
                }
            }

            if (alive)
                continue;

            lock (_gate)
                _entries.Remove(entry);

            if (entry.Location.Length > 0)
                _history.RecordClosed(entry.Location);

            ReleaseEntry(entry);
        }
    }

    // ------------------------------------------------------------------
    // New windows → tabs
    // ------------------------------------------------------------------

    /// <summary>
    /// EVENT_OBJECT_SHOW: a window became visible. New Explorer windows are made invisible
    /// right here - before they are painted - so a window that is about to become a tab
    /// never flashes on screen. Runs on the STA thread (it pumps).
    /// </summary>
    private void OnWindowShown(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0 || _disposed)
            return;

        try
        {
            var settings = Settings;
            if (!settings.Enabled || !settings.SingleWindowMode)
                return;

            if (Environment.TickCount64 - _startedAt < StartupGraceMs)
                return;

            if (NativeMethods.IsCtrlDown() && NativeMethods.IsShiftDown())
                return;

            if (!NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
                return;

            // A window we already track is not a new window (it is being shown again).
            int entryCount;
            lock (_gate)
            {
                if (_entries.Any(e => e.FrameHandle == hwnd))
                    return;
                entryCount = _entries.Count;
            }

            // Nothing to fold it into → it stays a window, don't touch it.
            if (entryCount == 0)
                return;

            if (WindowAnimator.Hide(hwnd))
            {
                lock (_gate)
                    _hiddenWindows[hwnd] = Environment.TickCount64;
            }
        }
        catch
        {
            // Never let the hook callback throw.
        }
    }

    /// <summary>ShellWindows.WindowRegistered: a new tab or window exists. STA thread.</summary>
    private void OnWindowRegistered(int cookie)
    {
        // Leave the event thread immediately; the shell is still inside Register().
        _ = Task.Run(ProcessRegisteredWindowAsync);
    }

    private void OnWindowRevoked(int cookie)
    {
        _ = Task.Run(() =>
        {
            try { PruneDeadEntries(); }
            catch { /* ignore */ }
        });
    }

    private async Task ProcessRegisteredWindowAsync()
    {
        TabEntry? entry = null;
        var keepHidden = false;

        try
        {
            var deliberateWindow = NativeMethods.IsCtrlDown() && NativeMethods.IsShiftDown();

            // The new entry may need a moment to surface in the collection.
            var deadline = Environment.TickCount64 + 2500;
            while (entry is null && Environment.TickCount64 < deadline)
            {
                entry = await _sta.RunWithTimeoutAsync<TabEntry?>(() =>
                {
                    TabEntry? found = null;
                    ForEachShellWindow(browser =>
                    {
                        if (found is not null || WasSeen(browser))
                            return;

                        MarkSeen(browser);
                        found = AddEntry(browser);
                    });
                    return found;
                }, null, 3000).ConfigureAwait(false);

                if (entry is null)
                    await Task.Delay(70).ConfigureAwait(false);
            }

            if (entry is null)
                return;

            // A brand new window can register before its first navigation finished, so its
            // location may still be empty; give NavigateComplete2 a moment to fill it in.
            if (entry.Location.Length == 0)
            {
                var found = entry;
                var locationDeadline = Environment.TickCount64 + 1500;
                while (found.Location.Length == 0 && Environment.TickCount64 < locationDeadline)
                {
                    await Task.Delay(60).ConfigureAwait(false);
                    if (found.Location.Length == 0)
                    {
                        await _sta.RunWithTimeoutAsync<bool>(() =>
                        {
                            try { found.Location = ReadLocation(found.Browser); }
                            catch { /* mid-navigation */ }
                            return true;
                        }, false, 1000).ConfigureAwait(false);
                    }
                }
            }

            var frame = entry.FrameHandle;
            var location = entry.Location;
            _log.Debug($"Shell window registered: tab 0x{entry.TabHandle:X} in 0x{frame:X} -> '{location}'.");

            var settings = Settings;

            if (deliberateWindow || !settings.Enabled || !settings.SingleWindowMode)
                return;

            if (Environment.TickCount64 - _startedAt < StartupGraceMs)
                return;

            // Without a location the window's content cannot be reproduced in a tab;
            // folding it would lose whatever the user opened. Leave it alone.
            if (location.Length == 0)
                return;

            // Control Panel is not a folder.
            if (location.StartsWith(PathNormalizer.ControlPanel, StringComparison.OrdinalIgnoreCase))
                return;

            if (location.Length > 0 && settings.IsExcluded(PathNormalizer.Normalize(location)))
                return;

            // Only fold a *window* (its first tab). A tab added to an existing window is
            // just tracked. And without another window there is nothing to fold into.
            int otherTabs;
            lock (_gate)
                otherTabs = _entries.Count(e => e.FrameHandle != frame);

            var isSingleTabWindow = frame != 0 &&
                                    ExplorerWindows.GetTabs(frame).Count == 1 &&
                                    frame != _mainWindow;

            if (otherTabs == 0 || !isSingleTabWindow)
                return;

            // A tab the user just dragged out of a window is a deliberate window too:
            // its folder shows up in the history as "closed" a moment ago.
            if (_history.WasClosedRecently(location, 2000))
                return;

            // From here on the window is ours: keep it invisible and move its folder over.
            if (WindowAnimator.Hide(frame))
            {
                lock (_gate)
                    _hiddenWindows[frame] = Environment.TickCount64;
            }

            entry.Folding = true;

            var result = await OpenAsTabAsync(location, activate: true, allowDuplicate: false,
                excludeFrame: frame).ConfigureAwait(false);

            if (result.Succeeded && result.Outcome != OpenOutcome.OpenedNewWindow)
            {
                keepHidden = true;

                await _sta.RunWithTimeoutAsync<bool>(() =>
                {
                    try { entry.Browser.Quit(); }
                    catch { /* it may already be gone */ }
                    return true;
                }, false, 3000).ConfigureAwait(false);

                Activity?.Invoke(this, result.Outcome == OpenOutcome.SwitchedToExistingTab
                    ? $"\"{PathNormalizer.ToDisplayName(location)}\" was already open - switched to its tab."
                    : $"Opened \"{PathNormalizer.ToDisplayName(location)}\" as a tab.");
            }
            else
            {
                entry.Folding = false;
                _log.Warn($"Window 0x{frame:X} ('{location}') could not be folded into a tab; it stays a window.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Handling a new Explorer window failed", ex);
        }
        finally
        {
            // Whatever happened: never leave an invisible window behind.
            if (!keepHidden && entry is not null)
                RevealWindow(entry.FrameHandle);

            RevealHiddenWindows(onlyExpired: true);
        }
    }

    private void RevealWindow(nint hwnd)
    {
        if (hwnd == 0)
            return;

        bool wasHidden;
        lock (_gate)
            wasHidden = _hiddenWindows.Remove(hwnd);

        if (wasHidden && NativeMethods.IsWindow(hwnd))
            WindowAnimator.Show(hwnd);
    }

    /// <summary>Shows hidden windows nobody claimed (safety net, like ETU's ShowAgain).</summary>
    private void RevealHiddenWindows(bool onlyExpired)
    {
        List<nint> stale;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            stale = _hiddenWindows
                .Where(p => !onlyExpired || now - p.Value > 4000)
                .Select(p => p.Key)
                .ToList();

            foreach (var hwnd in stale)
                _hiddenWindows.Remove(hwnd);
        }

        foreach (var hwnd in stale)
        {
            if (NativeMethods.IsWindow(hwnd))
                WindowAnimator.Show(hwnd);
        }
    }

    // ------------------------------------------------------------------
    // Opening folders
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows <paramref name="location"/> according to the rules: switch to the tab that
    /// already shows it, otherwise add a tab to the main window (special folders go to the
    /// left), otherwise open the first window.
    /// </summary>
    public async Task<OpenResult> OpenAsTabAsync(string location, bool activate = true,
        bool allowDuplicate = false, nint excludeFrame = 0, nint preferredWindow = 0)
    {
        var path = PathNormalizer.Normalize(location);
        if (path.Length == 0)
            path = ShellDefaults.GetLaunchLocation();

        await _openLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = Settings;
            var dedupe = !allowDuplicate &&
                         settings.WhenAlreadyOpen == AlreadyOpenBehavior.SwitchToExistingTab;

            if (dedupe)
            {
                var existing = await FindExistingTabAsync(path, excludeFrame).ConfigureAwait(false);
                if (existing is not null)
                {
                    await SelectTabByHandleAsync(existing.Value.Frame, existing.Value.Tab).ConfigureAwait(false);
                    if (activate)
                        WindowAnimator.Activate(existing.Value.Frame);

                    _history.Forget(path);
                    _log.Info($"Switched to the tab that already shows '{path}'.");
                    return new OpenResult(OpenOutcome.SwitchedToExistingTab, path,
                        existing.Value.Frame, existing.Value.Tab);
                }
            }

            var mainWindow = GetMainWindow(excludeFrame, preferredWindow);
            if (mainWindow == 0)
                return OpenNewWindow(path);

            var tabsBefore = ExplorerWindows.GetTabs(mainWindow);
            if (tabsBefore.Count == 0)
            {
                // No tab support (pre-22H2 build): a window is the only honest fallback.
                return OpenNewWindow(path);
            }

            // Ctrl+T. Explorer appends the new tab at the end of the strip and selects it.
            NativeMethods.PostMessage(tabsBefore[0], NativeMethods.WM_COMMAND,
                NativeMethods.ExplorerCommandNewTab, 0);

            var newTab = await WaitForNewTabAsync(mainWindow, tabsBefore, 2000).ConfigureAwait(false);
            if (newTab == 0)
            {
                _log.Warn("Explorer did not create a tab; opening a window instead.");
                return OpenNewWindow(path);
            }

            var entry = await WaitForEntryAsync(newTab, 2500).ConfigureAwait(false);
            if (entry is null)
            {
                // Without the COM object the tab cannot be navigated; take it back.
                NativeMethods.SendMessage(newTab, NativeMethods.WM_COMMAND,
                    NativeMethods.ExplorerCommandCloseTab, 0);
                _log.Warn("The new tab never registered with the shell; opening a window instead.");
                return OpenNewWindow(path);
            }

            var placed = false;
            if (settings.KeepSpecialFoldersFirst && !allowDuplicate &&
                SpecialFolders.Rank(path) is int rank)
            {
                placed = await TryPlaceSpecialFolderAsync(mainWindow, tabsBefore.Count, entry, path, rank)
                    .ConfigureAwait(false);
            }

            if (!placed)
                await NavigateAsync(entry, path).ConfigureAwait(false);

            if (activate)
                WindowAnimator.Activate(mainWindow);

            _history.Forget(path);
            _log.Info($"Opened '{path}' as a tab in 0x{mainWindow:X}.");
            return new OpenResult(OpenOutcome.OpenedNewTab, path, mainWindow, newTab);
        }
        catch (Exception ex)
        {
            _log.Error($"Opening '{path}' failed", ex);
            return new OpenResult(OpenOutcome.Failed, path, Message: ex.Message);
        }
        finally
        {
            _openLock.Release();
        }
    }

    /// <summary>Opens a real window (explicit request, or nothing to put a tab into).</summary>
    public OpenResult OpenNewWindow(string location)
    {
        var path = PathNormalizer.Normalize(location);
        if (path.Length == 0)
            path = ShellDefaults.GetLaunchLocation();

        try
        {
            NativeMethods.BypassForegroundLock();

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = false
            });

            _log.Info($"Opened '{path}' in a new window.");
            return new OpenResult(OpenOutcome.OpenedNewWindow, path);
        }
        catch (Exception ex)
        {
            _log.Error($"Opening a window for '{path}' failed", ex);
            return new OpenResult(OpenOutcome.Failed, path, Message: ex.Message);
        }
    }

    /// <summary>Opens a second tab with the folder of the active tab (duplicates allowed).</summary>
    public async Task<OpenResult> DuplicateActiveTabAsync(nint window = 0)
    {
        var frame = window != 0 ? window : GetMainWindow(0, ExplorerWindows.GetForegroundWindow());
        if (frame == 0)
            return new OpenResult(OpenOutcome.Failed, string.Empty, Message: "No File Explorer window is open.");

        var activeTab = ExplorerWindows.GetActiveTab(frame);
        var entry = FindEntryByTab(activeTab);
        if (entry is null || entry.Location.Length == 0)
            return new OpenResult(OpenOutcome.Failed, string.Empty, Message: "The active tab reports no folder.");

        return await OpenAsTabAsync(entry.Location, activate: true, allowDuplicate: true,
            preferredWindow: frame).ConfigureAwait(false);
    }

    /// <summary>Reopens the most recently closed folder as a tab.</summary>
    public async Task<OpenResult> ReopenLastClosedAsync(nint window = 0)
    {
        var closed = _history.TakeMostRecent();
        if (closed is null)
            return new OpenResult(OpenOutcome.Failed, string.Empty, Message: "No recently closed folder.");

        return await OpenAsTabAsync(closed.Path, activate: true, preferredWindow: window).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds every other window's tabs into the main window and closes those windows.
    /// Explicit action (tray menu / --consolidate), also used right after start.
    /// </summary>
    public async Task<int> ConsolidateAsync(nint keepWindow = 0)
    {
        var target = GetMainWindow(0, keepWindow != 0 ? keepWindow : ExplorerWindows.GetForegroundWindow());
        if (target == 0)
            return 0;

        var folded = 0;
        foreach (var frame in ExplorerWindows.GetWindows().Where(w => w != target).ToList())
        {
            List<TabEntry> tabs;
            lock (_gate)
                tabs = _entries.Where(e => e.FrameHandle == frame).ToList();

            if (tabs.Count == 0)
                continue;

            if (tabs.Any(t => t.Location.Length == 0) ||
                tabs.Any(t => Settings.IsExcluded(t.Location)) ||
                tabs.Any(t => t.Location.StartsWith(PathNormalizer.ControlPanel, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var allMoved = true;
            foreach (var tab in tabs)
            {
                tab.Folding = true;
                var result = await OpenAsTabAsync(tab.Location, activate: false,
                    excludeFrame: frame, preferredWindow: target).ConfigureAwait(false);

                if (!result.Succeeded || result.Outcome == OpenOutcome.OpenedNewWindow)
                {
                    tab.Folding = false;
                    allMoved = false;
                    break;
                }
            }

            if (!allMoved)
                continue;

            await _sta.RunWithTimeoutAsync<bool>(() =>
            {
                foreach (var tab in tabs)
                {
                    try { tab.Browser.Quit(); }
                    catch { /* already gone */ }
                }
                return true;
            }, false, 4000).ConfigureAwait(false);

            folded++;
        }

        if (folded > 0)
        {
            WindowAnimator.Activate(target);
            Activity?.Invoke(this, folded == 1
                ? "Folded an extra Explorer window into tabs."
                : $"Folded {folded} extra Explorer windows into tabs.");
        }

        return folded;
    }

    // ------------------------------------------------------------------
    // Dedupe / tab selection
    // ------------------------------------------------------------------

    /// <summary>The tab that already shows <paramref name="path"/>, or null.</summary>
    private async Task<(nint Frame, nint Tab)?> FindExistingTabAsync(string path, nint excludeFrame)
    {
        List<TabEntry> candidates;
        lock (_gate)
        {
            candidates = _entries
                .Where(e => !e.Folding &&
                            e.TabHandle != 0 &&
                            (excludeFrame == 0 || e.FrameHandle != excludeFrame) &&
                            e.Location.Length > 0)
                .ToList();
        }

        if (candidates.Count == 0)
            return null;

        // Fast textual pass first; then the shell compares PIDLs, exactly like Explorer
        // decides whether two paths are the same folder (short names, junctions, GUIDs).
        foreach (var entry in candidates)
        {
            if (string.Equals(entry.Location, path, StringComparison.OrdinalIgnoreCase))
                return Validate(entry);
        }

        var match = await _sta.RunWithTimeoutAsync<TabEntry?>(() =>
        {
            foreach (var entry in candidates)
            {
                if (_comparer is not null && _comparer.IsEquivalent(entry.Location, path))
                    return entry;
            }
            return null;
        }, null, 3000).ConfigureAwait(false);

        return match is null ? null : Validate(match);

        (nint Frame, nint Tab)? Validate(TabEntry entry) =>
            NativeMethods.IsWindow(entry.TabHandle)
                ? (entry.FrameHandle != 0 ? entry.FrameHandle : NativeMethods.GetParent(entry.TabHandle), entry.TabHandle)
                : null;
    }

    /// <summary>
    /// Brings one specific tab to the front of its window, ETU style: select position
    /// 0, 1, 2, ... until the tab in front is the right one (the z-order only tells us
    /// which tab is active, not where the others sit in the strip).
    /// </summary>
    private static async Task SelectTabByHandleAsync(nint frame, nint tab)
    {
        var tabs = ExplorerWindows.GetTabs(frame);
        if (tabs.Count == 0)
            return;

        var active = tabs[0];
        for (var i = 0; i < tabs.Count && active != tab; i++)
        {
            SelectTabByIndex(frame, i);
            active = await WaitForActiveTabChangeAsync(frame, active, 600).ConfigureAwait(false);
        }
    }

    /// <summary>Ctrl+1..n (0xA221): selects the tab at a 0-based strip position.</summary>
    private static void SelectTabByIndex(nint frame, int index) =>
        NativeMethods.SendMessage(frame, NativeMethods.WM_COMMAND,
            NativeMethods.ExplorerCommandSelectTab, index + 1);

    private static async Task<nint> WaitForActiveTabChangeAsync(nint frame, nint previous, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var active = NativeMethods.FindWindowEx(frame, 0, NativeMethods.ExplorerTabClass, null);
            if (active != 0 && active != previous)
                return active;

            await Task.Delay(30).ConfigureAwait(false);
        }

        return NativeMethods.FindWindowEx(frame, 0, NativeMethods.ExplorerTabClass, null);
    }

    private static async Task<nint> WaitForNewTabAsync(nint frame, IReadOnlyCollection<nint> before, int timeoutMs)
    {
        var known = before.ToHashSet();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var tab in ExplorerWindows.GetTabs(frame))
            {
                if (!known.Contains(tab))
                    return tab;
            }

            await Task.Delay(40).ConfigureAwait(false);
        }

        return 0;
    }

    private TabEntry? FindEntryByTab(nint tabHandle)
    {
        if (tabHandle == 0)
            return null;

        lock (_gate)
            return _entries.FirstOrDefault(e => e.TabHandle == tabHandle);
    }

    private async Task<TabEntry?> WaitForEntryAsync(nint tabHandle, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var entry = FindEntryByTab(tabHandle);
            if (entry is not null)
                return entry;

            // The WindowRegistered event normally beats us to it; this is the fallback for
            // the moment the event is still being dispatched.
            await _sta.RunWithTimeoutAsync<bool>(() =>
            {
                ForEachShellWindow(browser =>
                {
                    if (WasSeen(browser))
                        return;

                    MarkSeen(browser);
                    AddEntry(browser);
                });
                return true;
            }, false, 2000).ConfigureAwait(false);

            entry = FindEntryByTab(tabHandle);
            if (entry is not null)
                return entry;

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Navigation
    // ------------------------------------------------------------------

    /// <summary>Navigates one tab to a folder (Navigate2 on its shell window object).</summary>
    private async Task NavigateAsync(TabEntry entry, string path)
    {
        // The entry is updated optimistically so a follow-up dedupe within the next few
        // hundred milliseconds already sees the new folder; NavigateComplete2 corrects it.
        entry.Location = path;
        entry.Name = PathNormalizer.ToDisplayName(path);

        await _sta.RunWithTimeoutAsync<bool>(() =>
        {
            object url = path;
            object empty = Type.Missing;

            // Folder names with '#' break the URL form of Navigate2; hand the shell the
            // folder object instead (same trick as ETU).
            if (path.Contains('#'))
            {
                var folder = GetShellNamespace(path);
                if (folder is not null)
                    url = folder;
            }

            entry.Browser.Navigate2(ref url, ref empty, ref empty, ref empty, ref empty);
            return true;
        }, false, 5000).ConfigureAwait(false);
    }

    /// <summary>Shell.Application.NameSpace(path), late bound. STA thread.</summary>
    private static object? GetShellNamespace(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
                return null;

            var shell = Activator.CreateInstance(type);
            if (shell is null)
                return null;

            try
            {
                return shell.GetType().InvokeMember("NameSpace",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Special folder ordering (Home, This PC, Desktop, Downloads first)
    // ------------------------------------------------------------------

    /// <summary>
    /// Puts a freshly created tab's special folder at its fixed place on the left.
    /// </summary>
    /// <remarks>
    /// Explorer can only append tabs at the end of the strip, so the folder - not the tab -
    /// is moved: the contents of the leading tabs are shifted one place to the right (at
    /// most four navigations, the special group is never larger) and the special folder
    /// lands in the tab at its position. One normal folder may move to the new tab at the
    /// end; no folder is ever lost.
    /// <para>
    /// Positions: <c>k</c> = how many special tabs rank before the new one (its target
    /// position), <c>m</c> = how many special tabs exist. Specials always sit at positions
    /// <c>0..m-1</c> because this method is the only thing that places them.
    /// </para>
    /// </remarks>
    private async Task<bool> TryPlaceSpecialFolderAsync(nint frame, int tabCountBefore,
        TabEntry newEntry, string path, int rank)
    {
        try
        {
            int k, m;
            lock (_gate)
            {
                var ranks = _entries
                    .Where(e => e.FrameHandle == frame && e.TabHandle != newEntry.TabHandle)
                    .Select(e => SpecialFolders.Rank(e.Location))
                    .Where(r => r is not null)
                    .Select(r => r!.Value)
                    .ToList();

                m = ranks.Count;
                k = ranks.Count(r => r < rank);
            }

            // Already belongs at the end - the plain navigation puts it there.
            if (k >= tabCountBefore)
                return false;

            m = Math.Min(m, tabCountBefore);

            // The strip positions whose tab handles are needed: k..m-1 are the specials
            // that shift right; position m (the first normal tab) is displaced to the end
            // when it exists.
            var lastWalk = Math.Min(m, tabCountBefore - 1);

            var handles = new nint[lastWalk + 1];
            var previous = NativeMethods.FindWindowEx(frame, 0, NativeMethods.ExplorerTabClass, null);
            for (var i = k; i <= lastWalk; i++)
            {
                SelectTabByIndex(frame, i);
                var active = await WaitForActiveTabChangeAsync(frame, previous, 600).ConfigureAwait(false);
                if (active == 0 || active == newEntry.TabHandle)
                    return false;

                handles[i] = active;
                previous = active;
            }

            // Every position needs a known folder, otherwise a tab's content could be lost.
            var entries = new TabEntry[lastWalk + 1];
            for (var i = k; i <= lastWalk; i++)
            {
                var entry = FindEntryByTab(handles[i]);
                if (entry is null || entry.Location.Length == 0)
                    return false;
                entries[i] = entry;
            }

            // Shift right, starting from the end of the group.
            if (m < tabCountBefore)
            {
                // The first normal folder moves to the new tab at the end of the strip.
                await NavigateAsync(newEntry, entries[m].Location).ConfigureAwait(false);
                for (var i = m; i >= k + 1; i--)
                    await NavigateAsync(entries[i], entries[i - 1].Location).ConfigureAwait(false);
            }
            else
            {
                // Every tab is special: the new tab at the end is the group's new last slot.
                await NavigateAsync(newEntry, entries[m - 1].Location).ConfigureAwait(false);
                for (var i = m - 1; i >= k + 1; i--)
                    await NavigateAsync(entries[i], entries[i - 1].Location).ConfigureAwait(false);
            }

            // The target position receives the special folder and stays selected.
            await NavigateAsync(entries[k], path).ConfigureAwait(false);
            SelectTabByIndex(frame, k);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not place '{path}' at the left of the strip: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------

    /// <summary>The window every tab goes into.</summary>
    private nint GetMainWindow(nint excludeFrame, nint preferred = 0)
    {
        if (preferred != 0 && preferred != excludeFrame &&
            NativeMethods.HasClassName(preferred, NativeMethods.ExplorerWindowClass))
        {
            _mainWindow = preferred;
            return preferred;
        }

        if (_mainWindow != 0 && _mainWindow != excludeFrame &&
            NativeMethods.IsWindow(_mainWindow) &&
            NativeMethods.HasClassName(_mainWindow, NativeMethods.ExplorerWindowClass))
        {
            return _mainWindow;
        }

        // The oldest window with the most tabs is the one the user has been assembling.
        _mainWindow = ExplorerWindows.GetWindows()
            .Where(w => w != excludeFrame)
            .Reverse()
            .OrderByDescending(w => ExplorerWindows.GetTabs(w).Count)
            .FirstOrDefault();

        return _mainWindow;
    }

    // ------------------------------------------------------------------
    // Health / diagnostics
    // ------------------------------------------------------------------

    /// <summary>Recreates the shell objects when Explorer restarted, prunes dead tabs.</summary>
    private async void HealthCheck()
    {
        if (_disposed || !_started)
            return;

        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
            return;

        try
        {
            PruneDeadEntries();
            RevealHiddenWindows(onlyExpired: true);

            var healthy = await _sta.RunWithTimeoutAsync(() =>
            {
                try
                {
                    return _shellWindows is IShellWindows typed && typed.get_Count(out _) == 0;
                }
                catch
                {
                    return false;
                }
            }, false, 4000).ConfigureAwait(false);

            if (!healthy)
            {
                _log.Warn("The shell stopped answering (Explorer restart?); reconnecting.");
                await _sta.RunAsync(() =>
                {
                    DisposeShellObjects();
                    InitializeShellObjects();
                }).ConfigureAwait(false);
            }
            else
            {
                // Windows that appeared while we were blind (missed events).
                await _sta.RunWithTimeoutAsync<bool>(() =>
                {
                    ForEachShellWindow(browser =>
                    {
                        if (WasSeen(browser))
                            return;

                        MarkSeen(browser);
                        AddEntry(browser);
                    });
                    return true;
                }, false, 3000).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Watcher health check failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }

    /// <summary>Everything the watcher currently knows, one line per tab (for --probe).</summary>
    public List<string> DescribeTabs()
    {
        lock (_gate)
        {
            return _entries
                .Select(e => $"window 0x{e.FrameHandle:X}  tab 0x{e.TabHandle:X}  " +
                             $"'{e.Location}' ({(e.Name.Length > 0 ? e.Name : "?")})")
                .ToList();
        }
    }

    public int TrackedTabCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        _sta.Dispose();
        _openLock.Dispose();
    }
}
