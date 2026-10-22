using System.Collections.Concurrent;
using System.Threading.Channels;
using WinExplorer.Hooks;
using WinExplorer.Interop;
using WinExplorer.Shell;

namespace WinExplorer.Services;

/// <summary>
/// Keeps File Explorer to a single window.
/// </summary>
/// <remarks>
/// <para>
/// This is the approach of w4po/ExplorerTabUtility, which is the most reliable one for
/// Windows 11 in practice: instead of trying to stop Explorer from creating a window, the
/// window is caught the moment it is shown, made invisible <b>before</b> anything is asked of
/// the shell, folded into a tab of the window that is already open, and then quit. Because
/// the hiding happens in the window-show callback - before the window gets a chance to be
/// painted or to finish loading - the user does not see a flash, and a second window for a
/// folder that is already a tab cannot survive.
/// </para>
/// <para>
/// Folding runs on one worker, in arrival order: two windows appearing at once cannot race
/// each other into two tabs for the same folder. A slow sweep (every few seconds) only ever
/// looks for windows that slipped past the hook, and it is skipped while the user's hands are
/// busy with a new-window gesture. Tab bookkeeping (the "reopen closed tab" history) is a
/// separate, cheap poll.
/// </para>
/// </remarks>
internal sealed class SingleWindowEngine : IDisposable
{
    private readonly Logger _log;
    private readonly ExplorerService _service;
    private readonly TabController _tabs;

    private readonly NativeMethods.WinEventProc _showCallback;
    private nint _showHook;

    private readonly Channel<nint> _queue = Channel.CreateUnbounded<nint>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _workCancellation;

    private readonly ConcurrentDictionary<nint, byte> _queued = new();
    private readonly ConcurrentDictionary<nint, byte> _cloaked = new();
    private readonly ConcurrentDictionary<nint, byte> _processed = new();

    private readonly HashSet<nint> _knownWindows = new();
    private readonly Dictionary<nint, string> _knownTabs = new();
    private readonly object _gate = new();

    private System.Threading.Timer? _sweepTimer;
    private System.Threading.Timer? _pollTimer;
    private int _sweeping;
    private int _polling;
    private bool _disposed;
    private long _startedAt;

    public SingleWindowEngine(Logger log, ExplorerService service, TabController tabs)
    {
        _log = log;
        _service = service;
        _tabs = tabs;
        _showCallback = OnWindowShown;
    }

    public bool IsRunning => _showHook != 0;

    /// <summary>Raised when a window was folded into a tab (for the UI and the log).</summary>
    public event EventHandler<string>? Merged;

    public void Start()
    {
        if (_showHook != 0)
            return;

        // Windows are only interesting once they are shown: that is the first moment the
        // handle exists, and the earliest moment it can be made invisible again.
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            0, _showCallback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_showHook == 0)
            _log.Warn("Could not install the Explorer window hook; only the sweep will run.");

        _startedAt = Environment.TickCount64;

        // Windows that are already open are none of our business.
        foreach (var window in ExplorerWindows.GetWindows())
            _knownWindows.Add(window);

        _ = Task.Run(SeedKnownTabsAsync);

        _workCancellation = new CancellationTokenSource();
        _ = Task.Run(() => WorkLoopAsync(_workCancellation.Token));

        _sweepTimer = new System.Threading.Timer(_ => Sweep(), null, 4000, 4000);
        _pollTimer = new System.Threading.Timer(_ => Poll(), null, 3000, 3000);

        _log.Info("Explorer watcher started.");
    }

    public void Stop()
    {
        if (_showHook != 0)
        {
            NativeMethods.UnhookWinEvent(_showHook);
            _showHook = 0;
        }

        _sweepTimer?.Dispose();
        _sweepTimer = null;

        _pollTimer?.Dispose();
        _pollTimer = null;

        _workCancellation?.Cancel();
        _workCancellation = null;

        // Never leave a window invisible behind us.
        foreach (var window in _cloaked.Keys.ToList())
        {
            WindowAnimator.Show(window);
            _cloaked.TryRemove(window, out _);
        }

        _queued.Clear();
        _log.Info("Explorer watcher stopped.");
    }

    // ------------------------------------------------------------------
    // Catching new windows
    // ------------------------------------------------------------------

    private void OnWindowShown(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == 0 || _disposed)
            return;

        try
        {
            if (!NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
                return;

            if (!IsAutomatic)
                return;

            // Our own windows (the one the merge itself creates) and windows the user asked
            // for must be left alone.
            if (_tabs.IsSelfCreatedWindow(hwnd))
            {
                _processed[hwnd] = 0;
                return;
            }

            if (!_knownWindows.Add(hwnd))
                return;

            // Sessions restored at sign-in arrive in bulk in the first seconds; folding those
            // would be surprising, and they are already on screen.
            if (Environment.TickCount64 - _startedAt < 8000)
            {
                _log.Debug($"Window 0x{hwnd:X} appeared during startup; kept as a window.");
                return;
            }

            if (IsDeliberateWindow())
            {
                _log.Debug($"Window 0x{hwnd:X} is a deliberate new window; kept.");
                return;
            }

            if (_service.IsPinned(hwnd))
                return;

            // Only interesting when there is something to fold it into.
            if (OtherWindows(hwnd).Count == 0)
                return;

            // Hide it *now*, before any shell call: this is what keeps the window from ever
            // being seen, and what stops Explorer from finishing a visible render of it.
            var cloaked = WindowAnimator.Hide(hwnd);
            if (cloaked)
                _cloaked[hwnd] = 0;

            if (!_queued.TryAdd(hwnd, 0))
                return;

            if (!_queue.Writer.TryWrite(hwnd))
                _queued.TryRemove(hwnd, out _);
        }
        catch (Exception ex)
        {
            _log.Error("Window hook error", ex);
        }
    }

    private async Task WorkLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var hwnd in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _queued.TryRemove(hwnd, out _);

                try
                {
                    await FoldAsync(hwnd, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Error($"Folding window 0x{hwnd:X} failed", ex);
                    Reveal(hwnd);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task FoldAsync(nint hwnd, CancellationToken cancellationToken)
    {
        // The window exists before its tab does, and a busy shell sometimes needs a moment
        // longer, so the fold is attempted twice. The window stays invisible meanwhile, so
        // none of this is visible to the user.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                Reveal(hwnd);
                return;
            }

            await WaitForFirstTabAsync(hwnd, 1200, cancellationToken).ConfigureAwait(false);

            if (await _service.MergeWindowAsync(hwnd, cancellationToken).ConfigureAwait(false))
            {
                _cloaked.TryRemove(hwnd, out _);
                _processed[hwnd] = 0;
                Merged?.Invoke(this, "Folded a new window into a tab.");
                return;
            }

            if (attempt == 0)
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        // Left as a window on purpose (an excluded or virtual folder, a gesture we did not
        // see, a folder that never became readable). It must be visible again - never a
        // transparent window the user cannot find.
        Reveal(hwnd);
        _log.Debug($"Window 0x{hwnd:X} kept as a window.");
    }

    private static async Task WaitForFirstTabAsync(nint hwnd, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd) || ExplorerWindows.GetTabs(hwnd).Count > 0)
                return;

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Reveal(nint hwnd)
    {
        if (_cloaked.TryRemove(hwnd, out _))
            WindowAnimator.Show(hwnd);
    }

    // ------------------------------------------------------------------
    // Safety sweep and bookkeeping
    // ------------------------------------------------------------------

    /// <summary>
    /// Catches windows that never produced a show event we could use (a tab dragged out of a
    /// window, a window created while the app was starting, a shell that was slow). Runs
    /// rarely: the hook above is the main path.
    /// </summary>
    private async void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
            return;

        try
        {
            if (!IsAutomatic)
                return;

            if (Environment.TickCount64 - _startedAt < 8000)
                return;

            // Do not fight the user's hands.
            if (IsDeliberateWindow())
                return;

            var windows = ExplorerWindows.GetWindows();
            PruneKnownWindows(windows);

            if (windows.Count < 2)
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
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    /// <summary>Remembers tabs that disappear, for "reopen closed tab".</summary>
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

            // A completely empty answer while windows exist is a shell hiccup, not ten tabs
            // being closed at once.
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

                    if (NativeMethods.IsWindow(tabHwnd))
                        continue;

                    _knownTabs.Remove(tabHwnd);
                    if (!string.IsNullOrEmpty(location))
                        closed.Add(location);
                }
            }

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

    private void PruneKnownWindows(List<nint> windows)
    {
        var alive = windows.ToHashSet();
        lock (_gate)
        {
            foreach (var known in _knownWindows.Where(h => !alive.Contains(h)).ToList())
                _knownWindows.Remove(known);
        }

        foreach (var stale in _processed.Keys.Where(h => !alive.Contains(h)).ToList())
            _processed.TryRemove(stale, out _);

        foreach (var stale in _cloaked.Keys.Where(h => !alive.Contains(h)).ToList())
            _cloaked.TryRemove(stale, out _);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Single-window behaviour is on (the master switch or the older pair).</summary>
    private bool IsAutomatic
    {
        get
        {
            var settings = _service.Settings;
            return settings.Enabled && (settings.SingleWindowMode || settings.MergeNewWindows);
        }
    }

    /// <summary>True while the user is asking for a separate window on purpose.</summary>
    private static bool IsDeliberateWindow() =>
        (NativeMethods.IsCtrlDown() && NativeMethods.IsShiftDown()) ||
        MouseHook.NewWindowGestureRecently() ||
        MouseHook.FolderRightClickRecently(1500);

    private static List<nint> OtherWindows(nint except) =>
        ExplorerWindows.GetWindows().Where(h => h != except).ToList();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
