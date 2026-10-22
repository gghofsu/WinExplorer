using System.Diagnostics;
using WinExplorer.Core;
using WinExplorer.Interop;
using WinExplorer.Services;

namespace WinExplorer.Shell;

/// <summary>
/// Everything that actually touches File Explorer: create/navigate/select/close tabs and
/// open or focus windows.
/// </summary>
/// <remarks>
/// Tabs are driven through the commands Explorer's own accelerators use:
/// <list type="bullet">
/// <item><c>WM_COMMAND 0xA21B</c> on the active tab window = Ctrl+T</item>
/// <item><c>WM_COMMAND 0xA221</c> with lParam = n on the window = Ctrl+n</item>
/// <item><c>WM_COMMAND 0xA021</c> on a tab window = Ctrl+W</item>
/// </list>
/// Navigation itself goes through <c>Shell.Application</c> (<c>Navigate2</c>) because that
/// is what lets us address one specific tab. These ids are undocumented; every step is
/// therefore verified and falls back to "open a window" instead of failing silently.
/// </remarks>
internal sealed class TabController : IDisposable
{
    private readonly StaExecutor _querySta;
    private readonly StaExecutor _navSta;
    private readonly Logger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<nint, long> _selfCreatedWindows = new();

    // Explorer refuses a tab now and then while it is busy (a folder is still loading, a
    // dialog is open, the window just appeared). One failure must never switch tabs off for
    // the rest of the session, so failures are counted and reset by the next success.
    private int _consecutiveTabFailures;
    private bool _unsupportedWarned;

    public TabController(Logger log)
    {
        _log = log;

        // The logger is passed down so a wedged shell call is visible in the log instead of
        // silently turning every location into an empty string.
        _querySta = new StaExecutor("WinExplorer.Shell.Query", 6000, log);
        _navSta = new StaExecutor("WinExplorer.Shell.Navigate", 8000, log);
    }

    /// <summary>Health of the shell threads, for --diagnose / --probe.</summary>
    public string DescribeShellHealth() =>
        $"query thread {_querySta.ThreadName}, {_querySta.HungCalls} abandoned call(s); " +
        $"navigate thread {_navSta.ThreadName}, {_navSta.HungCalls} abandoned call(s); " +
        ShellCom.Describe();

    /// <summary>Raised with a human readable message for the UI/tray.</summary>
    public event EventHandler<string>? Activity;

    /// <summary>
    /// True once Explorer refused to create a tab several times in a row (tabs need Windows
    /// 11 22H2+, or this Explorer build does not answer the tab command). Callers use it to
    /// avoid hiding windows; they still try again, because the next success clears it.
    /// </summary>
    public bool TabsUnsupported => _consecutiveTabFailures >= 3;

    /// <summary>How often a tab was refused in a row (0 when tabs work).</summary>
    public int ConsecutiveTabFailures => _consecutiveTabFailures;

    /// <summary>Windows WinExplorer opened itself; the window merger must ignore them.</summary>
    public bool IsSelfCreatedWindow(nint hwnd)
    {
        lock (_selfCreatedWindows)
        {
            PruneSelfCreated();
            return _selfCreatedWindows.ContainsKey(hwnd);
        }
    }

    private void PruneSelfCreated()
    {
        var now = Environment.TickCount64;
        foreach (var key in _selfCreatedWindows.Where(p => now - p.Value > 8000).Select(p => p.Key).ToList())
            _selfCreatedWindows.Remove(key);
    }

    private void RememberSelfCreated(nint hwnd)
    {
        if (hwnd == 0)
            return;

        lock (_selfCreatedWindows)
        {
            PruneSelfCreated();
            _selfCreatedWindows[hwnd] = Environment.TickCount64;
        }
    }

    // ------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens <paramref name="location"/> in a new tab of <paramref name="windowHwnd"/> and
    /// returns the new tab. Returns null when Explorer did not cooperate (caller decides
    /// whether to fall back to a window).
    /// </summary>
    public async Task<ExplorerTab?> OpenTabAsync(nint windowHwnd, string location, bool activate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (windowHwnd == 0 || !NativeMethods.IsWindow(windowHwnd) ||
                !NativeMethods.HasClassName(windowHwnd, NativeMethods.ExplorerWindowClass))
            {
                _log.Warn($"No usable Explorer window (0x{windowHwnd:X}); cannot add a tab.");
                return null;
            }

            var tabsBefore = ExplorerWindows.GetTabs(windowHwnd);

            // The tab the user is looking at: it gets the Ctrl+T command (Explorer opens the
            // new tab next to it) and is put back in front when the new tab opens in the
            // background.
            var activeTab = ExplorerWindows.GetActiveTab(windowHwnd);
            var commandTarget = activeTab != 0 ? activeTab : windowHwnd;

            // Same command as Ctrl+T, posted to the active tab window.
            NativeMethods.PostMessage(commandTarget, NativeMethods.WM_COMMAND, NativeMethods.ExplorerCommandNewTab, 0);

            var newTab = await WaitForNewTabAsync(windowHwnd, tabsBefore, 2500, cancellationToken).ConfigureAwait(false);
            if (newTab == 0)
            {
                // Second attempt, this time synchronously to the window itself.
                _log.Warn("Explorer did not create a tab on the first try; retrying.");
                NativeMethods.SendMessageTimeout(windowHwnd, NativeMethods.WM_COMMAND,
                    NativeMethods.ExplorerCommandNewTab, 0, NativeMethods.SMTO_ABORTIFHUNG, 1500, out _);

                newTab = await WaitForNewTabAsync(windowHwnd, tabsBefore, 2000, cancellationToken).ConfigureAwait(false);
            }

            if (newTab == 0 && _consecutiveTabFailures == 0)
            {
                // Tabs normally work, so this is a hiccup: give Explorer one more, slower try
                // before falling back to a window.
                _log.Warn("Explorer did not create a tab; retrying once more, slowly.");
                await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                NativeMethods.SendMessageTimeout(windowHwnd, NativeMethods.WM_COMMAND,
                    NativeMethods.ExplorerCommandNewTab, 0, NativeMethods.SMTO_ABORTIFHUNG, 2500, out _);

                newTab = await WaitForNewTabAsync(windowHwnd, tabsBefore, 3000, cancellationToken).ConfigureAwait(false);
            }

            if (newTab == 0)
            {
                // Cap the counter: it only decides "tabs look unsupported", and a session
                // that keeps failing must not overflow it.
                var failures = _consecutiveTabFailures >= 100
                    ? _consecutiveTabFailures
                    : Interlocked.Increment(ref _consecutiveTabFailures);
                if (failures >= 3 && !_unsupportedWarned)
                {
                    _unsupportedWarned = true;
                    _log.Warn("Explorer keeps refusing to create tabs (tabs need Windows 11 22H2 or later).");
                }
                else
                {
                    _log.Warn($"Explorer did not create a tab (failure {failures} in a row).");
                }

                return null;
            }

            Interlocked.Exchange(ref _consecutiveTabFailures, 0);

            ExplorerWindows.InvalidateCache();
            var tab = new ExplorerTab(newTab, windowHwnd, string.Empty, PathNormalizer.ToDisplayName(location));

            // The new tab is navigated through the shell window item that owns it, so it does
            // not matter whether Explorer activated it or left it in the background.
            var navigated = await NavigateAndConfirmAsync(windowHwnd, newTab, location, cancellationToken)
                .ConfigureAwait(false);

            if (!navigated)
            {
                // Leaving a tab on Home behind would be worse than no tab at all: close it
                // and let the caller pick another route (the current tab, or a window).
                _log.Warn($"Tab 0x{newTab:X} could not be navigated to '{location}'; closing it again.");

                // Closing the right tab needs it to be the one showing.
                await EnsureActiveTabAsync(windowHwnd, newTab, cancellationToken).ConfigureAwait(false);
                NativeMethods.PostMessage(newTab, NativeMethods.WM_COMMAND, NativeMethods.ExplorerCommandCloseTab, 0);
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                ExplorerWindows.InvalidateCache();

                if (activeTab != 0 && NativeMethods.IsWindow(activeTab))
                {
                    await SelectTabCoreAsync(windowHwnd, activeTab, activateWindow: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                return null;
            }

            var actual = await _querySta.RunWithTimeoutAsync<string?>(() => ShellCom.GetTabLocation(newTab), null, 2000)
                .ConfigureAwait(false);
            tab = tab with { Location = string.IsNullOrEmpty(actual) ? PathNormalizer.Normalize(location) : actual };

            if (activate)
            {
                await SelectTabCoreAsync(windowHwnd, newTab, activateWindow: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (activeTab != 0 && activeTab != newTab && NativeMethods.IsWindow(activeTab))
            {
                // A background open: put the tab the user was looking at back in front.
                await SelectTabCoreAsync(windowHwnd, activeTab, activateWindow: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            ExplorerWindows.InvalidateCache();
            _log.Info($"Opened tab 0x{newTab:X} in window 0x{windowHwnd:X} -> '{location}'.");
            Activity?.Invoke(this, $"Opened \"{PathNormalizer.ToDisplayName(location)}\" in a new tab.");
            return tab;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Error("Opening a tab failed", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Navigates the active tab of <paramref name="windowHwnd"/> to <paramref name="location"/>.
    /// The last resort when Explorer will not create a tab: the folder still ends up in the
    /// window that is already open instead of in a new window.
    /// </summary>
    public async Task<bool> NavigateActiveTabAsync(nint windowHwnd, string location,
        CancellationToken cancellationToken = default)
    {
        var tabs = ExplorerWindows.GetTabs(windowHwnd);
        if (tabs.Count == 0)
            return false;

        var tab = tabs[0];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var navigated = await _navSta.RunAsync(() => ShellCom.NavigateTab(tab, location))
                .ConfigureAwait(false);

            if (navigated)
            {
                ExplorerWindows.InvalidateCache();
                _log.Info($"Navigated tab 0x{tab:X} of window 0x{windowHwnd:X} to '{location}'.");
            }

            return navigated;
        }
        catch (Exception ex)
        {
            _log.Error("Navigating the active tab failed", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Switches the window to <paramref name="tabHwnd"/>.</summary>
    public async Task<bool> SelectTabAsync(nint windowHwnd, nint tabHwnd, bool activateWindow,
        CancellationToken cancellationToken = default)
    {
        if (windowHwnd == 0 || tabHwnd == 0)
            return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SelectTabCoreAsync(windowHwnd, tabHwnd, activateWindow, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="tabHwnd"/> the tab that is showing, and waits until Explorer
    /// agrees. Everything that navigates a tab depends on this.
    /// </summary>
    private async Task<bool> EnsureActiveTabAsync(nint windowHwnd, nint tabHwnd, CancellationToken cancellationToken)
    {
        if (ExplorerWindows.GetActiveTab(windowHwnd) == tabHwnd)
            return true;

        if (!await SelectTabCoreAsync(windowHwnd, tabHwnd, activateWindow: false, cancellationToken)
                .ConfigureAwait(false))
        {
            _log.Debug($"Could not activate tab 0x{tabHwnd:X} before navigating it.");
            return false;
        }

        return await WaitForActiveTabAsync(windowHwnd, tabHwnd, 500, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Navigates the active tab to <paramref name="location"/> and verifies it landed there.
    /// Retries once: a brand new tab is often not registered with the shell for a moment.
    /// </summary>
    private async Task<bool> NavigateAndConfirmAsync(nint windowHwnd, nint tabHwnd, string location,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var navigated = await _navSta
                .RunWithTimeoutAsync(() => ShellCom.NavigateTab(tabHwnd, location), false, 4000)
                .ConfigureAwait(false);

            if (!navigated)
                _log.Debug($"Navigate2 for 0x{tabHwnd:X} was refused (attempt {attempt}).");

            var confirmed = await _querySta
                .RunWithTimeoutAsync(() => ShellCom.WaitForLocation(tabHwnd, location, 2000), false, 3000)
                .ConfigureAwait(false);

            if (confirmed)
                return true;

            var current = await _querySta.RunWithTimeoutAsync<string?>(() => ShellCom.GetTabLocation(tabHwnd), null, 1500)
                .ConfigureAwait(false);

            if (ShellCom.LocationsMatch(current, location))
                return true;

            // Second route, for builds where the item cannot be tied to a tab: activate the tab
            // and let the shell navigate the one that is showing.
            if (attempt == 2)
            {
                await EnsureActiveTabAsync(windowHwnd, tabHwnd, cancellationToken).ConfigureAwait(false);
                await _navSta.RunWithTimeoutAsync(() => ShellCom.NavigateTab(tabHwnd, location), false, 4000)
                    .ConfigureAwait(false);

                var afterActivation = await _querySta
                    .RunWithTimeoutAsync(() => ShellCom.WaitForLocation(tabHwnd, location, 1500), false, 2500)
                    .ConfigureAwait(false);

                if (afterActivation)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Selection without taking the gate; the caller must hold it.</summary>
    private async Task<bool> SelectTabCoreAsync(nint windowHwnd, nint tabHwnd, bool activateWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            var tabs = ExplorerWindows.GetTabs(windowHwnd);
            if (!tabs.Contains(tabHwnd))
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                tabs = ExplorerWindows.GetTabs(windowHwnd);
            }

            if (!tabs.Contains(tabHwnd))
            {
                _log.Warn($"Tab 0x{tabHwnd:X} is not part of window 0x{windowHwnd:X}.");
                return false;
            }

            if (ExplorerWindows.GetActiveTab(windowHwnd) != tabHwnd)
            {
                // Ctrl+<n> walks the tab strip; index 1 is the first *tab window*, which is
                // not necessarily the target, so verify after every step.
                var maxSteps = Math.Min(tabs.Count, 32);
                for (var i = 0; i < maxSteps; i++)
                {
                    // Guard against the crash Explorer has when the index exceeds the tab
                    // strip while tabs are still being created.
                    if (i + 1 > ExplorerWindows.GetTabs(windowHwnd).Count)
                        break;

                    // Synchronous (with a hang guard) so the switch has actually happened
                    // before we look at the tab again.
                    NativeMethods.SendMessageTimeout(windowHwnd, NativeMethods.WM_COMMAND,
                        NativeMethods.ExplorerCommandSelectTab, (nint)(i + 1),
                        NativeMethods.SMTO_ABORTIFHUNG, 800, out _);

                    if (await WaitForActiveTabAsync(windowHwnd, tabHwnd, 350, cancellationToken).ConfigureAwait(false))
                        break;
                }
            }

            var selected = ExplorerWindows.GetActiveTab(windowHwnd) == tabHwnd;
            if (activateWindow)
                WindowAnimator.Activate(windowHwnd);

            if (selected)
                _log.Debug($"Selected tab 0x{tabHwnd:X} in window 0x{windowHwnd:X}.");
            else
                _log.Warn($"Could not select tab 0x{tabHwnd:X} in window 0x{windowHwnd:X}.");

            return selected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Error("Switching tabs failed", ex);
            return false;
        }
    }

    /// <summary>Closes a tab (Ctrl+W equivalent). Used when cleaning up duplicates.</summary>
    public async Task<bool> CloseTabAsync(nint windowHwnd, nint tabHwnd, CancellationToken cancellationToken = default)
    {
        if (windowHwnd == 0 || tabHwnd == 0)
            return false;

        if (!await SelectTabAsync(windowHwnd, tabHwnd, activateWindow: false, cancellationToken).ConfigureAwait(false))
            return false;

        NativeMethods.PostMessage(tabHwnd, NativeMethods.WM_COMMAND, NativeMethods.ExplorerCommandCloseTab, 0);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        ExplorerWindows.InvalidateCache();
        return !NativeMethods.IsWindow(tabHwnd);
    }

    /// <summary>Location currently shown by a tab (empty when unknown).</summary>
    public Task<string?> GetTabLocationAsync(nint tabHwnd, CancellationToken cancellationToken = default) =>
        _querySta.RunWithTimeoutAsync<string?>(() => ShellCom.GetTabLocation(tabHwnd), null, 2500);

    /// <summary>The location shown by the active tab of a window.</summary>
    public Task<string?> GetActiveLocationAsync(nint windowHwnd, CancellationToken cancellationToken = default) =>
        _querySta.RunWithTimeoutAsync<string?>(() => ShellCom.GetActiveTabLocation(windowHwnd), null, 2500);

    /// <summary>
    /// The folder of every tab of <paramref name="windowHwnd"/>, in tab order.
    /// </summary>
    /// <remarks>
    /// Shells that report one entry *per window* can only describe the tab that is showing,
    /// so a background tab is temporarily activated to read it - the window is about to be
    /// folded away, and the tab that was active is restored afterwards.
    /// </remarks>
    public async Task<List<string>> GetTabLocationsAsync(nint windowHwnd, CancellationToken cancellationToken = default)
    {
        var locations = new List<string>();
        var tabs = ExplorerWindows.GetTabs(windowHwnd);
        if (tabs.Count == 0)
            return locations;

        var originalActive = tabs[0];

        foreach (var tab in tabs)
        {
            if (cancellationToken.IsCancellationRequested)
                return locations;

            var location = await GetTabLocationAsync(tab, cancellationToken).ConfigureAwait(false);

            // Activating tabs is visible, so only do it in a window the user is not using.
            var userIsWatching = ExplorerWindows.GetForegroundWindow() == windowHwnd;
            if (string.IsNullOrEmpty(location) && tabs.Count > 1 && !userIsWatching)
            {
                // Make it the active tab, then ask again (the shell now describes this tab).
                if (await SelectTabAsync(windowHwnd, tab, activateWindow: false, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                    ShellCom.InvalidateMemo();
                    location = await GetTabLocationAsync(tab, cancellationToken).ConfigureAwait(false);
                }
            }

            locations.Add(location ?? string.Empty);
        }

        // Leave the window as the user had it.
        if (originalActive != 0 && NativeMethods.IsWindow(originalActive) &&
            ExplorerWindows.GetActiveTab(windowHwnd) != originalActive)
        {
            await SelectTabAsync(windowHwnd, originalActive, activateWindow: false, cancellationToken)
                .ConfigureAwait(false);
        }

        return locations;
    }

    /// <summary>Runs a shell query on the dedicated STA thread (abandoned if the shell hangs).</summary>
    public Task<T> RunOnQueryThreadAsync<T>(Func<T> func) =>
        _querySta.RunWithTimeoutAsync(func, default!, 4000);

    /// <summary>Confirms with the shell that both locations are the same folder.</summary>
    public Task<bool> AreSameLocationAsync(string? a, string? b, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return Task.FromResult(false);

        return _querySta.RunWithTimeoutAsync(() => ShellCom.LocationsMatch(a, b), false, 3000);
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens <paramref name="location"/> in a brand new File Explorer window and returns
    /// its handle (0 on failure).
    /// </summary>
    public async Task<nint> OpenNewWindowAsync(string location, CancellationToken cancellationToken = default)
    {
        var before = ExplorerWindows.GetWindows().ToHashSet();
        _log.Info($"Opening a new window for '{location}'.");

        var launched = false;
        try
        {
            launched = ShellExecuteNewWindow(location);
            if (!launched)
                launched = LaunchExplorer(location);
        }
        catch (Exception ex)
        {
            _log.Error("Launching a new window failed", ex);
        }

        if (!launched)
            return 0;

        nint found = 0;
        var deadline = Environment.TickCount64 + 4000;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = ExplorerWindows.GetWindows().FirstOrDefault(h => !before.Contains(h));
            if (window != 0)
            {
                // Give Explorer a moment to finish restoring the window before we touch it.
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                ExplorerWindows.InvalidateCache();
                found = window;
                break;
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        if (found != 0)
        {
            RememberSelfCreated(found);
            _log.Info($"New window 0x{found:X} for '{location}'.");
            Activity?.Invoke(this, $"Opened \"{PathNormalizer.ToDisplayName(location)}\" in a new window.");
        }
        else
        {
            _log.Warn($"No new Explorer window appeared for '{location}'.");
        }

        return found;
    }

    private static bool ShellExecuteNewWindow(string location)
    {
        // Only ever use "opennewwindow": the plain "open" verb may be redirected back to
        // WinExplorer when the folder context menu integration is installed.
        var info = new SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_FLAG_NO_UI | NativeMethods.SEE_MASK_NOASYNC |
                    NativeMethods.SEE_MASK_INVOKEIDLIST | NativeMethods.SEE_MASK_NOZONECHECKS,
            lpVerb = "opennewwindow",
            lpFile = location,
            nShow = NativeMethods.SW_SHOWNORMAL
        };

        return NativeMethods.ShellExecuteEx(ref info);
    }

    private static bool LaunchExplorer(string location)
    {
        try
        {
            // "/n" forces a new window even for shell namespace paths, and it can never be
            // redirected back into WinExplorer (unlike the "open" shell verb).
            var start = new ProcessStartInfo("explorer.exe", $"/n,\"{location}\"")
            {
                UseShellExecute = true
            };
            Process.Start(start)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Brings an Explorer window to the foreground (restoring it if needed).</summary>
    public Task<bool> ActivateWindowAsync(nint windowHwnd) =>
        Task.Run(() => WindowAnimator.Activate(windowHwnd));

    /// <summary>
    /// Asks a window to close (used after its content was folded into a tab). The active
    /// tab is closed rather than the window, so extra tabs are never lost silently.
    /// </summary>
    public async Task<bool> CloseWindowAsync(nint windowHwnd, CancellationToken cancellationToken = default)
    {
        if (windowHwnd == 0 || !NativeMethods.IsWindow(windowHwnd))
            return true;

        var tabs = ExplorerWindows.GetTabs(windowHwnd);
        var target = tabs.Count > 0 ? tabs[0] : windowHwnd;
        NativeMethods.PostMessage(target, NativeMethods.WM_COMMAND, NativeMethods.ExplorerCommandCloseTab, 0);
        NativeMethods.PostMessage(windowHwnd, NativeMethods.WM_CLOSE, 0, 0);

        var deadline = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.IsWindow(windowHwnd))
                return true;
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        return !NativeMethods.IsWindow(windowHwnd);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<nint> WaitForNewTabAsync(nint windowHwnd, IReadOnlyCollection<nint> before,
        int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tabs = ExplorerWindows.GetTabs(windowHwnd);
            foreach (var tab in tabs)
            {
                if (!before.Contains(tab))
                    return tab;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<bool> WaitForActiveTabAsync(nint windowHwnd, nint expected, int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ExplorerWindows.GetActiveTab(windowHwnd) == expected)
                return true;

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return ExplorerWindows.GetActiveTab(windowHwnd) == expected;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _navSta.Dispose();
        _querySta.Dispose();
    }
}
