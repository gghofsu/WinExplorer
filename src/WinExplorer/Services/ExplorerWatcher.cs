using System.Collections.Concurrent;
using WinExplorer.Hooks;
using WinExplorer.Interop;
using WinExplorer.Shell;

namespace WinExplorer.Services;

/// <summary>
/// Watches File Explorer: new windows are offered to the merger, and disappearing tabs are
/// recorded in the "reopen closed tab" history.
/// </summary>
internal sealed class ExplorerWatcher : IDisposable
{
    private readonly Logger _log;
    private readonly ExplorerService _service;
    private readonly TabController _tabs;

    private readonly NativeMethods.WinEventProc _showCallback;
    private nint _showHook;
    private System.Threading.Timer? _pollTimer;

    // The single-window pass has its own timer: the poll above does tab bookkeeping and
    // returns early in several cases, and folding a stray window must never wait for that.
    private System.Threading.Timer? _singleWindowTimer;
    private int _consolidating;
    private bool _disposed;

    private readonly HashSet<nint> _knownWindows = new();
    private readonly Dictionary<nint, string> _knownTabs = new();
    private readonly ConcurrentDictionary<nint, byte> _pendingMerges = new();
    private readonly object _gate = new();
    private int _polling;
    private long _startedAt;

    public ExplorerWatcher(Logger log, ExplorerService service, TabController tabs)
    {
        _log = log;
        _service = service;
        _tabs = tabs;
        _showCallback = OnWindowShown;
    }

    public bool IsRunning => _showHook != 0;

    /// <summary>Raised when a tab was turned into a merged window (for the UI/log).</summary>
    public event EventHandler<string>? Merged;

    public void Start()
    {
        if (_showHook != 0)
            return;

        // Windows are only interesting once they are shown: that is when Explorer has a
        // location to report and when the user would otherwise see a window flash.
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            0, _showCallback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_showHook == 0)
            _log.Warn("Could not install the Explorer window hook.");

        _startedAt = Environment.TickCount64;

        // Seed the "already known" sets so pre-existing windows are not merged.
        foreach (var window in ExplorerWindows.GetWindows())
            _knownWindows.Add(window);

        _ = Task.Run(SeedKnownTabsAsync);

        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 2000, 1500);
        _singleWindowTimer = new System.Threading.Timer(_ => ConsolidateTick(), null, 2000, 600);
        _log.Info("Explorer watcher started.");
    }

    public void Stop()
    {
        if (_showHook != 0)
        {
            NativeMethods.UnhookWinEvent(_showHook);
            _showHook = 0;
        }

        _pollTimer?.Dispose();
        _pollTimer = null;

        _singleWindowTimer?.Dispose();
        _singleWindowTimer = null;
        _log.Info("Explorer watcher stopped.");
    }

    private async Task SeedKnownTabsAsync()
    {
        try
        {
            var tabs = await _tabs.RunOnQueryThreadAsync(() => ExplorerWindows.EnumerateTabs(useCache: false))
                .ConfigureAwait(false);

            lock (_gate)
            {
                foreach (var tab in tabs)
                    _knownTabs[tab.TabHwnd] = tab.Location;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not read the open tabs at startup", ex);
        }
    }

    private void OnWindowShown(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0)
            return;

        try
        {
            if (!NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
                return;

            if (!_pendingMerges.TryAdd(hwnd, 0))
                return;

            // Run off the hook callback: the merge waits for Explorer to catch up.
            ThreadPool.QueueUserWorkItem(async state =>
            {
                try
                {
                    await HandleNewWindowAsync(hwnd).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error($"Merging window 0x{hwnd:X} failed", ex);
                }
                finally
                {
                    _pendingMerges.TryRemove(hwnd, out _);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error("Window hook error", ex);
        }
    }

    private async Task HandleNewWindowAsync(nint hwnd)
    {
        bool alreadyKnown;
        lock (_gate)
        {
            alreadyKnown = !_knownWindows.Add(hwnd);
        }

        if (alreadyKnown)
            return;

        // Sessions restored at sign-in arrive in bulk right after we start; folding those
        // into tabs would be surprising. Leave the first seconds alone.
        if (Environment.TickCount64 - _startedAt < 8000)
        {
            _log.Debug($"Window 0x{hwnd:X} appeared during startup; kept as a window.");
            return;
        }

        // Give Explorer a beat to finish creating the window (and its first tab).
        await Task.Delay(40).ConfigureAwait(false);

        if (!NativeMethods.IsWindow(hwnd))
            return;

        var merged = await _service.MergeWindowAsync(hwnd).ConfigureAwait(false);
        if (merged)
            Merged?.Invoke(this, "Merged a window into a tab.");
        else
            _log.Debug($"Window 0x{hwnd:X} kept as a window.");
    }

    private async void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;

        try
        {
            var windows = ExplorerWindows.GetWindows();
            PruneKnownWindows(windows);

            var tabs = await _tabs.RunOnQueryThreadAsync(() => ExplorerWindows.EnumerateTabs(useCache: false))
                .ConfigureAwait(false);

            // A completely empty answer while windows exist is a shell hiccup, not ten
            // tabs being closed at once.
            if (tabs.Count == 0 && windows.Count > 0)
                return;

            var seen = new HashSet<nint>();
            var closed = new List<string>();

            lock (_gate)
            {
                foreach (var tab in tabs)
                {
                    seen.Add(tab.TabHwnd);
                    if (!string.IsNullOrEmpty(tab.Location))
                        _knownTabs[tab.TabHwnd] = tab.Location;
                }

                foreach (var (tabHwnd, location) in _knownTabs.ToList())
                {
                    if (seen.Contains(tabHwnd))
                        continue;

                    // Still alive: the shell simply has not listed it yet.
                    if (NativeMethods.IsWindow(tabHwnd))
                        continue;

                    _knownTabs.Remove(tabHwnd);
                    if (!string.IsNullOrEmpty(location))
                        closed.Add(location);
                }
            }

            // Explorer restarting takes every tab with it; do not fill the history.
            if (closed.Count > 3 && windows.Count == 0)
            {
                _log.Info("Explorer restarted; keeping the tab history clean.");
                return;
            }

            foreach (var location in closed)
                _service.NotifyTabClosed(location);
        }
        catch (Exception ex)
        {
            _log.Error("Explorer watcher poll failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>
    /// Single window mode: fold extra windows that were not opened on purpose. Runs on its
    /// own timer, so a second window disappears into a tab within a second or two even while
    /// the poll above is busy or skipping a pass.
    /// </summary>
    private async void ConsolidateTick()
    {
        if (Interlocked.Exchange(ref _consolidating, 1) == 1)
            return;

        try
        {
            var settings = _service.Settings;
            if (!settings.Enabled)
                return;

            // Single window mode is the master switch; the older switches apply when it is off.
            var automatic = settings.SingleWindowMode ||
                            (settings.MergeNewWindows && settings.MergeExistingWindows);
            if (!automatic)
                return;

            // Windows restored at sign-in arrive in bulk; give the session a few seconds.
            if (Environment.TickCount64 - _startedAt < 8000)
                return;

            // Do not fight the user's hands: a modified double-click or a folder context menu
            // (Open in new window) may be about to create a window that will be pinned.
            if (MouseHook.NewWindowGestureRecently() || MouseHook.FolderRightClickRecently(2500))
                return;

            if (ExplorerWindows.GetWindows().Count < 2)
                return;

            var folded = await _service.ConsolidateWindowsAsync().ConfigureAwait(false);
            if (folded > 0)
            {
                Merged?.Invoke(this, folded == 1
                    ? "Folded an extra window into a tab."
                    : $"Folded {folded} extra windows into tabs.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("The single-window pass failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _consolidating, 0);
        }
    }

    private void PruneKnownWindows(List<nint> windows)
    {
        var alive = windows.ToHashSet();
        lock (_gate)
        {
            foreach (var known in _knownWindows.Where(h => !alive.Contains(h)).ToList())
                _knownWindows.Remove(known);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
