using WinExplorer.Core;
using WinExplorer.Hooks;
using WinExplorer.Interop;
using WinExplorer.Shell;

namespace WinExplorer.Services;

/// <summary>
/// The policy layer. Decides *what* should happen for a folder (switch to the tab that
/// already shows it, add a tab, open a window, fold a window into a tab) and delegates the
/// mechanics to <see cref="TabController"/>.
/// </summary>
internal sealed class ExplorerService : IDisposable
{
    private readonly TabController _tabs;
    private readonly SettingsStore _settings;
    private readonly Logger _log;
    private readonly TabHistory _history;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly SemaphoreSlim _consolidateGate = new(1, 1);
    private readonly Dictionary<string, long> _suppressedClosures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _suppressGate = new();

    // Windows the user asked for explicitly (Shift+double-click, Open in new window). They
    // are never folded automatically - the whole point of the gesture is a separate window.
    private readonly HashSet<nint> _pinnedWindows = new();
    private readonly object _pinGate = new();

    // Windows that could not be folded (Explorer refused the tab). They are retried after a
    // pause instead of every second, so a stubborn window cannot cause a create/close loop.
    private readonly Dictionary<nint, long> _foldBackoff = new();

    public ExplorerService(TabController tabs, SettingsStore settings, Logger log, TabHistory? history = null)
    {
        _tabs = tabs;
        _settings = settings;
        _log = log;
        _history = history ?? new TabHistory();
    }

    /// <summary>Raised after every open attempt, for logging/notifications.</summary>
    public event EventHandler<OpenResult>? Opened;

    /// <summary>Raised when something noteworthy happened that the UI should show.</summary>
    public event EventHandler<string>? Status;

    public AppSettings Settings => _settings.Current;

    public TabHistory History => _history;

    // ------------------------------------------------------------------
    // Folders
    // ------------------------------------------------------------------

    public async Task<OpenResult> OpenAsync(OpenRequest request, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var path = PathNormalizer.Normalize(request.Path);

        if (path.Length == 0)
            return Report(new OpenResult(OpenOutcome.Failed, string.Empty, Message: "No folder was given."), request);

        if (!settings.Enabled)
        {
            // Paused: behave like plain Windows would (still honour an explicit request so
            // the context menu keeps working while the helper is paused).
            var window = await _tabs.OpenNewWindowAsync(path, cancellationToken).ConfigureAwait(false);
            return Report(new OpenResult(window != 0 ? OpenOutcome.OpenedNewWindow : OpenOutcome.Failed,
                path, window, Message: "WinExplorer is paused."), request);
        }

        if (request.Mode == OpenMode.NewWindow)
            return await OpenInNewWindowAsync(path, request, cancellationToken).ConfigureAwait(false);

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allowDuplicate = request.Mode == OpenMode.NewTabInExistingWindow ||
                                 settings.WhenAlreadyOpen == AlreadyOpenBehavior.OpenAnotherTab;

            if (!allowDuplicate)
            {
                var existing = await _tabs.RunOnQueryThreadAsync(() => ExplorerWindows.FindTab(
                    path, request.PreferredWindow, settings.DedupeAcrossAllWindows, request.ExcludeWindow))
                    .ConfigureAwait(false);

                if (existing is not null &&
                    await _tabs.SelectTabAsync(existing.WindowHwnd, existing.TabHwnd, request.Activate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    // Clicking a folder that is already open must not create a duplicate.
                    _history.Forget(path);
                    _log.Info($"Switched to tab 0x{existing.TabHwnd:X} for '{path}'.");
                    return Report(new OpenResult(OpenOutcome.SwitchedToExistingTab, path,
                        existing.WindowHwnd, existing.TabHwnd), request);
                }
            }

            var targetWindow = await ChooseTargetWindowAsync(settings, request, cancellationToken).ConfigureAwait(false);
            if (targetWindow == 0)
            {
                // Nothing open at all: the first folder has to create the first window.
                return await OpenInNewWindowAsync(path, request, cancellationToken).ConfigureAwait(false);
            }

            var activate = request.Activate && (settings.ActivateNewTab || request.Mode != OpenMode.ReuseExistingTab);
            var tab = await _tabs.OpenTabAsync(targetWindow, path, activate, cancellationToken).ConfigureAwait(false);

            if (tab is null)
            {
                // Explorer refused to add a tab. A second window would break the one-window
                // rule, so show the folder in the window that is already open when that does
                // not throw away another tab of it.
                var tabsInTarget = ExplorerWindows.GetTabs(targetWindow);
                if (tabsInTarget.Count <= 1 &&
                    await _tabs.NavigateActiveTabAsync(targetWindow, path, cancellationToken).ConfigureAwait(false))
                {
                    _history.Forget(path);
                    _log.Info($"No tab could be created; '{path}' was opened in the current tab.");
                    return Report(new OpenResult(OpenOutcome.OpenedNewTab, path, targetWindow),
                        request);
                }

                _log.Warn($"Falling back to a new window for '{path}'.");
                return await OpenInNewWindowAsync(path, request, cancellationToken).ConfigureAwait(false);
            }

            _history.Forget(path);
            return Report(new OpenResult(OpenOutcome.OpenedNewTab, path, targetWindow, tab.TabHwnd), request);
        }
        catch (OperationCanceledException)
        {
            return new OpenResult(OpenOutcome.Failed, path, Message: "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error($"Opening '{path}' failed", ex);
            return Report(new OpenResult(OpenOutcome.Failed, path, Message: ex.Message), request);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task<OpenResult> OpenInNewWindowAsync(string path, OpenRequest request,
        CancellationToken cancellationToken)
    {
        var window = await _tabs.OpenNewWindowAsync(path, cancellationToken).ConfigureAwait(false);
        if (window == 0)
            return Report(new OpenResult(OpenOutcome.Failed, path, Message: "Windows did not open a window."), request);

        // An explicit "new window" request (--new-window, the context menu) must survive the
        // single-window pass. Windows created only as a fallback are not pinned.
        if (request.Mode == OpenMode.NewWindow)
            Pin(window);

        if (request.Activate)
            await _tabs.ActivateWindowAsync(window).ConfigureAwait(false);

        _history.Forget(path);
        return Report(new OpenResult(OpenOutcome.OpenedNewWindow, path, window), request);
    }

    private async Task<nint> ChooseTargetWindowAsync(AppSettings settings, OpenRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PreferredWindow != 0 && NativeMethods.HasClassName(request.PreferredWindow,
                NativeMethods.ExplorerWindowClass) && request.PreferredWindow != request.ExcludeWindow)
        {
            return request.PreferredWindow;
        }

        var window = await _tabs.RunOnQueryThreadAsync(() => ExplorerWindows.ChooseTargetWindow(settings))
            .ConfigureAwait(false);

        if (window == request.ExcludeWindow)
            window = ExplorerWindows.GetWindows().FirstOrDefault(w => w != request.ExcludeWindow);

        return window;
    }

    // ------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------

    /// <summary>Duplicates the tab the user is looking at (allowed to create duplicates).</summary>
    public async Task<OpenResult> DuplicateActiveTabAsync(nint windowHwnd, CancellationToken cancellationToken = default)
    {
        var window = windowHwnd != 0 ? windowHwnd : ExplorerWindows.GetForegroundWindow();
        if (window == 0)
            window = ExplorerWindows.GetMostRecentWindow();

        if (window == 0)
            return ReportFailure("There is no File Explorer window to duplicate a tab from.");

        var location = await _tabs.GetActiveLocationAsync(window, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(location))
            return ReportFailure("The active tab has no folder to duplicate (is it still loading?).");

        _log.Info($"Duplicating the active tab of 0x{window:X} ('{location}').");
        return await OpenAsync(OpenRequest.NewTab(location, OpenOrigin.DuplicateTab, window), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OpenResult> ReopenLastClosedAsync(nint windowHwnd, CancellationToken cancellationToken = default)
    {
        var closed = _history.TakeMostRecent();
        if (closed is null)
            return ReportFailure("There is no recently closed folder to reopen.");

        _log.Info($"Reopening '{closed.Path}'.");
        return await OpenAsync(OpenRequest.NewTab(closed.Path, OpenOrigin.ReopenClosed, windowHwnd), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Records a folder as closed. Called by the watcher when a tab disappears.</summary>
    public void NotifyTabClosed(string? location)
    {
        var path = PathNormalizer.Normalize(location);
        if (path.Length == 0)
            return;

        lock (_suppressGate)
        {
            if (_suppressedClosures.TryGetValue(path, out var tick) && Environment.TickCount64 - tick < 4000)
            {
                _suppressedClosures.Remove(path);
                return;
            }
        }

        _history.RecordClosed(path);
        _log.Debug($"Tab closed: '{path}' (history: {_history.Count}).");
    }

    /// <summary>
    /// Marks a folder whose tab we closed ourselves, so it does not show up in
    /// "reopen closed tab" (it is still open in another tab).
    /// </summary>
    public void SuppressNextClosure(string? location)
    {
        var path = PathNormalizer.Normalize(location);
        if (path.Length == 0)
            return;

        lock (_suppressGate)
        {
            var now = Environment.TickCount64;
            foreach (var stale in _suppressedClosures.Where(p => now - p.Value > 10_000).Select(p => p.Key).ToList())
                _suppressedClosures.Remove(stale);
            _suppressedClosures[path] = now;
        }
    }

    // ------------------------------------------------------------------
    // Window merging
    // ------------------------------------------------------------------

    /// <summary>
    /// Folds a newly opened Explorer window into a tab of an existing window.
    /// Returns true when the window was consumed.
    /// </summary>
    public async Task<bool> MergeWindowAsync(nint newWindow, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.Enabled || !(settings.MergeNewWindows || settings.SingleWindowMode))
            return false;

        if (newWindow == 0 || !NativeMethods.IsWindow(newWindow))
            return false;

        if (_tabs.IsSelfCreatedWindow(newWindow))
            return false;

        var tabs = ExplorerWindows.GetTabs(newWindow);

        // This path only knows how to move the folder of the active tab, so a window with
        // several tabs is left to the consolidation pass, which moves every one of them.
        if (tabs.Count > 1)
        {
            _log.Debug($"Merge skipped: window 0x{newWindow:X} already has {tabs.Count} tabs.");
            return false;
        }

        string? location = null;
        var deadline = Environment.TickCount64 + 2500;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            location = await _tabs.GetActiveLocationAsync(newWindow, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(location))
                break;

            await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(location))
        {
            _log.Debug($"Merge skipped: window 0x{newWindow:X} never reported a folder.");
            return false;
        }

        if (settings.KeepVirtualFoldersAsWindows && PathNormalizer.IsShellNamespace(location))
        {
            _log.Debug($"Merge skipped: '{location}' is a virtual folder.");
            return false;
        }

        if (settings.IsExcluded(location))
        {
            _log.Debug($"Merge skipped: '{location}' is on the exclusion list.");
            return false;
        }

        // Dragging a tab out of a window creates a new window on purpose, and so does
        // "Open in new window": the tab was just detached/closed from somewhere.
        if (_history.Snapshot().Any(c => string.Equals(c.Path, location, StringComparison.OrdinalIgnoreCase) &&
                                         DateTimeOffset.Now - c.ClosedAt < TimeSpan.FromSeconds(2)))
        {
            Pin(newWindow);
            _log.Info($"Window 0x{newWindow:X} ('{PathNormalizer.ToDisplayName(location)}') kept: " +
                      "it was just detached from a window.");
            return false;
        }

        // The explicit "give me a window" gestures. The window stays pinned afterwards, so
        // the consolidation pass does not undo the user's deliberate choice either.
        if (ShouldKeepAsWindow(newWindow, location, out var reason))
        {
            _log.Info($"Window 0x{newWindow:X} ('{PathNormalizer.ToDisplayName(location)}') kept " +
                      $"as a window: {reason}.");
            return false;
        }

        var target = await ChooseTargetWindowAsync(settings, new OpenRequest
        {
            Path = location,
            ExcludeWindow = newWindow
        }, cancellationToken).ConfigureAwait(false);

        if (target == 0 || target == newWindow)
        {
            _log.Debug("Merge skipped: there is no other Explorer window to merge into.");
            return false;
        }

        // Hiding only helps when the window is about to disappear; with tabs known to be
        // broken the window has to be shown again a moment later, which looks worse.
        var hidden = !_tabs.TabsUnsupported && WindowAnimator.Hide(newWindow);
        try
        {
            var result = await OpenAsync(new OpenRequest
            {
                Path = location,
                Mode = OpenMode.ReuseExistingTab,
                Origin = OpenOrigin.WindowMerge,
                PreferredWindow = target,
                ExcludeWindow = newWindow,
                Activate = false
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                if (hidden)
                    WindowAnimator.Show(newWindow);
                return false;
            }

            SuppressNextClosure(location);
            ExplorerWindows.InvalidateCache();

            var closed = await _tabs.CloseWindowAsync(newWindow, cancellationToken).ConfigureAwait(false);
            if (!closed && hidden)
                WindowAnimator.Show(newWindow);

            _log.Info($"Merged window 0x{newWindow:X} ('{location}') into window 0x{target:X}.");
            Status?.Invoke(this, $"Merged \"{PathNormalizer.ToDisplayName(location)}\" into a tab.");
            return true;
        }
        catch (Exception ex)
        {
            if (hidden)
                WindowAnimator.Show(newWindow);
            _log.Error("Merging a window failed", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Window gestures and consolidation
    // ------------------------------------------------------------------

    /// <summary>
    /// True when a window must be left alone because the user asked for it: a modified
    /// double-click just happened, "Open in new window" was used on this very folder,
    /// Ctrl+Shift is held, or the window was already pinned for one of those reasons.
    /// </summary>
    private bool ShouldKeepAsWindow(nint window, string? location, out string reason)
    {
        if (NativeMethods.IsCtrlDown() && NativeMethods.IsShiftDown())
        {
            reason = "Ctrl+Shift is held";
            Pin(window);
            return true;
        }

        if (MouseHook.NewWindowGestureRecently())
        {
            reason = "Shift/modifier + double-click";
            Pin(window);
            return true;
        }

        // "Open in new window" from the context menu. Only a right-click on *this* folder
        // counts (a context menu can stay open for a while, hence the generous window); a
        // right-click somewhere else must not leave a stray window behind for ever.
        if (MouseHook.FolderRightClickMatches(location))
        {
            reason = "right-click -> Open in new window";
            Pin(window);
            return true;
        }

        if (IsPinned(window))
        {
            reason = "pinned earlier";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private void Pin(nint window)
    {
        if (window == 0)
            return;

        lock (_pinGate)
        {
            if (_pinnedWindows.Count > 32)
                _pinnedWindows.Clear(); // stale handles must not grow forever
            _pinnedWindows.Add(window);
        }
    }

    /// <summary>True when the window was opened on purpose and must not be folded.</summary>
    public bool IsPinned(nint window)
    {
        if (window == 0)
            return false;

        lock (_pinGate)
            return _pinnedWindows.Contains(window);
    }

    private void PrunePinnedWindows(IReadOnlyCollection<nint> alive)
    {
        lock (_pinGate)
        {
            foreach (var window in _pinnedWindows.Where(h => !alive.Contains(h)).ToList())
                _pinnedWindows.Remove(window);

            foreach (var window in _foldBackoff.Keys.Where(h => !alive.Contains(h)).ToList())
                _foldBackoff.Remove(window);
        }
    }

    /// <summary>True when this window already failed to fold and should be left alone for a while.</summary>
    private bool IsBackingOff(nint window)
    {
        lock (_pinGate)
        {
            return _foldBackoff.TryGetValue(window, out var since) &&
                   Environment.TickCount64 - since < 60000;
        }
    }

    private void BackOff(nint window, string reason)
    {
        lock (_pinGate)
            _foldBackoff[window] = Environment.TickCount64;

        _log.Warn($"Window 0x{window:X} stays a window for now: {reason}");
    }

    /// <summary>
    /// Folds every *other* Explorer window into one tabbed window, so only a single window
    /// is left. Windows the user opened on purpose (pinned) are left alone.
    /// </summary>
    /// <returns>How many windows were folded away.</returns>
    public async Task<int> ConsolidateWindowsAsync(nint keepWindow = 0, bool force = false,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.Enabled)
            return 0;

        // Single window mode is the master switch: while it is on, extra windows are always
        // folded. The two older switches only matter when the mode is off. The tray item and
        // the command line are explicit requests, so they always work.
        var automatic = settings.SingleWindowMode ||
                        (settings.MergeNewWindows && settings.MergeExistingWindows);
        if (!force && !automatic)
            return 0;

        // One pass at a time: the watcher polls, and the tray command can fire as well.
        if (!await _consolidateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return 0;

        try
        {
            ExplorerWindows.InvalidateCache();
            var windows = ExplorerWindows.GetWindows();
            PrunePinnedWindows(windows);

            var candidates = windows.Where(w => !IsPinned(w)).ToList();
            if (candidates.Count < 2)
                return 0;

            var target = ChooseConsolidationTarget(windows, candidates, keepWindow);
            if (target == 0)
                return 0;

            var activeTab = ExplorerWindows.GetActiveTab(target);
            var folded = 0;

            foreach (var window in candidates.Where(w => w != target))
            {
                if (!NativeMethods.IsWindow(window))
                    continue;

                if (await FoldWindowIntoAsync(window, target, cancellationToken).ConfigureAwait(false))
                {
                    folded++;
                }
                else if (candidates.Count - folded > 1)
                {
                    // Reported once per pass at debug level; the reason itself is logged by
                    // FoldWindowIntoAsync. It is picked up by --diagnose if it keeps happening.
                    _log.Debug($"Window 0x{window:X} could not be folded into 0x{target:X}.");
                }
            }

            if (folded > 0)
            {
                ExplorerWindows.InvalidateCache();

                // Leave the user looking at the tab they had before the extra windows came in.
                if (activeTab != 0 && NativeMethods.IsWindow(activeTab))
                    await _tabs.SelectTabAsync(target, activeTab, activateWindow: false, cancellationToken)
                        .ConfigureAwait(false);

                _log.Info($"Consolidated into window 0x{target:X}: {folded} extra window(s) folded into tabs.");
                Status?.Invoke(this, folded == 1
                    ? "Folded an extra Explorer window into a tab."
                    : $"Folded {folded} extra Explorer windows into tabs.");
            }

            return folded;
        }
        catch (Exception ex)
        {
            _log.Error("Consolidating the Explorer windows failed", ex);
            return 0;
        }
        finally
        {
            _consolidateGate.Release();
        }
    }

    private nint ChooseConsolidationTarget(List<nint> windows, List<nint> candidates, nint keepWindow)
    {
        if (keepWindow != 0 && candidates.Contains(keepWindow))
            return keepWindow;

        var foreground = ExplorerWindows.GetForegroundWindow();
        if (foreground != 0 && candidates.Contains(foreground))
            return foreground;

        // The window with the most tabs is the one the user has been assembling.
        var busiest = ExplorerWindows.Enumerate()
            .Where(w => candidates.Contains(w.Hwnd))
            .OrderByDescending(w => w.Tabs.Count)
            .ThenBy(w => windows.IndexOf(w.Hwnd))
            .Select(w => w.Hwnd)
            .FirstOrDefault();

        return busiest != 0 ? busiest : candidates[0];
    }

    /// <summary>Moves every tab of <paramref name="window"/> into <paramref name="target"/> and closes it.</summary>
    private async Task<bool> FoldWindowIntoAsync(nint window, nint target, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        if (IsBackingOff(window))
            return false;

        var locations = new List<string>();
        var tabs = ExplorerWindows.GetTabs(window);

        if (tabs.Count == 0)
        {
            // Explorer did not list the tabs (it is still starting, or the window is busy).
            // The window itself still reports its folder, which is just as good.
            var active = await _tabs.GetActiveLocationAsync(window, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(active))
            {
                _log.Debug($"Consolidation postponed for 0x{window:X}: it reports no folder yet.");
                return false;
            }

            locations.Add(PathNormalizer.Normalize(active));
        }
        else
        {
            foreach (var tab in tabs)
            {
                var location = await _tabs.GetTabLocationAsync(tab, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(location))
                    continue;

                locations.Add(PathNormalizer.Normalize(location));
            }
        }

        if (locations.Count != (tabs.Count == 0 ? 1 : tabs.Count))
        {
            // Some tabs could not be read, so they could not be recreated. A single tab
            // window still knows its folder through the window itself; anything else is
            // postponed until Explorer reports all of its tabs.
            if (tabs.Count <= 1 && locations.Count == 0)
            {
                var active = await _tabs.GetActiveLocationAsync(window, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(active))
                    locations.Add(PathNormalizer.Normalize(active));
            }

            if (locations.Count == 0 || tabs.Count > 1)
            {
                _log.Debug($"Consolidation postponed for 0x{window:X}: its tabs are not readable yet.");
                return false;
            }
        }

        // Closing this window would take its remaining tabs with it, so leave the whole
        // window alone when any of its folders must keep normal Windows behaviour.
        if (locations.Any(settings.IsExcluded) ||
            (settings.KeepVirtualFoldersAsWindows && locations.Any(PathNormalizer.IsShellNamespace)))
        {
            _log.Debug($"Consolidation skipped 0x{window:X}: a folder in it keeps normal Windows behaviour.");
            return false;
        }

        foreach (var location in locations)
        {
            // Already open in the surviving window: nothing to add, just drop the duplicate.
            if (ExplorerWindows.FindTab(location, target, acrossAllWindows: false) is not null)
            {
                SuppressNextClosure(location);
                continue;
            }

            var result = await OpenAsync(new OpenRequest
            {
                Path = location,
                Mode = OpenMode.NewTabInExistingWindow,
                Origin = OpenOrigin.WindowMerge,
                PreferredWindow = target,
                ExcludeWindow = window,
                Activate = false
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                BackOff(window, $"'{PathNormalizer.ToDisplayName(location)}' could not be opened.");
                return false;
            }

            if (result.Outcome == OpenOutcome.OpenedNewWindow)
            {
                // Explorer refused the tab and opened yet another window. Keep the window we
                // were trying to fold (it shows the same folder) instead of piling up more.
                BackOff(window, "Explorer refused to create a tab.");
                if (result.Window != 0 && result.Window != window)
                    await _tabs.CloseWindowAsync(result.Window, cancellationToken).ConfigureAwait(false);
                return false;
            }

            SuppressNextClosure(location);
        }

        ExplorerWindows.InvalidateCache();

        // Explorer closes a window by itself once its last tab has moved to the other
        // window, which makes the close fail on a handle that no longer exists.
        if (!NativeMethods.IsWindow(window))
            return true;

        if (!await _tabs.CloseWindowAsync(window, cancellationToken).ConfigureAwait(false))
        {
            _log.Debug($"Consolidation could not close window 0x{window:X}; retrying later.");
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------

    private OpenResult Report(OpenResult result, OpenRequest request)
    {
        Opened?.Invoke(this, result);
        if (_settings.Current.ShowNotifications && result.Succeeded)
            Status?.Invoke(this, Describe(result));
        return result;
    }

    private OpenResult ReportFailure(string message)
    {
        _log.Warn(message);
        var result = new OpenResult(OpenOutcome.Failed, string.Empty, Message: message);
        Opened?.Invoke(this, result);
        return result;
    }

    public static string Describe(OpenResult result) => result.Outcome switch
    {
        OpenOutcome.SwitchedToExistingTab =>
            $"Switched to \"{PathNormalizer.ToDisplayName(result.Path)}\".",
        OpenOutcome.OpenedNewTab =>
            $"Opened \"{PathNormalizer.ToDisplayName(result.Path)}\" in a new tab.",
        OpenOutcome.OpenedNewWindow =>
            $"Opened \"{PathNormalizer.ToDisplayName(result.Path)}\" in a new window.",
        _ => result.Message ?? "Could not open the folder."
    };

    public void Dispose()
    {
        _openGate.Dispose();
        _consolidateGate.Dispose();
    }
}
