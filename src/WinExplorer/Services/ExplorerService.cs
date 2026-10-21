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
    private readonly Dictionary<string, long> _suppressedClosures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _suppressGate = new();

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
                // Explorer refused to add a tab (Windows 10, or a broken Explorer state).
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
        if (!settings.Enabled || !settings.MergeNewWindows)
            return false;

        if (newWindow == 0 || !NativeMethods.IsWindow(newWindow))
            return false;

        if (_tabs.TabsUnsupported)
        {
            _log.Debug("Merge skipped: this build of Windows does not support tabs.");
            return false;
        }

        if (_tabs.IsSelfCreatedWindow(newWindow))
            return false;

        // Ctrl+Shift is the "I really want a window" gesture, and a right-click a moment
        // ago most likely means "Open in new window" from the context menu.
        if (NativeMethods.IsCtrlDown() && NativeMethods.IsShiftDown())
            return false;
        if (MouseHook.RightButtonReleasedRecently())
            return false;

        var tabs = ExplorerWindows.GetTabs(newWindow);
        if (tabs.Count > 1)
            return false; // a window the user assembled; leave it alone

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

        // Re-opening / detaching a tab also produces a window; do not swallow those.
        if (_history.Snapshot().Any(c => string.Equals(c.Path, location, StringComparison.OrdinalIgnoreCase) &&
                                         DateTimeOffset.Now - c.ClosedAt < TimeSpan.FromSeconds(2)))
        {
            _log.Debug($"Merge skipped: '{location}' was just closed/detached.");
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

        var hidden = WindowAnimator.Hide(newWindow);
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

    public void Dispose() => _openGate.Dispose();
}
