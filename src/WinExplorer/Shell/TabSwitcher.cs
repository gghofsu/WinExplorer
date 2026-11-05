namespace WinExplorer.Shell;

/// <summary>
/// The common existing-tab path for external opens and both early/late user-tab dedupe.
/// Restore/activate first, confirm selection, then refresh: a refresh sent while Explorer
/// is still switching views (or before a minimized window is restored) can be ignored.
/// Delegates keep the ordering independent of Win32/COM and regression-testable.
/// </summary>
internal static class TabSwitcher
{
    public static async Task<bool> SwitchAsync(nint frame, nint tab, bool activate,
        Func<nint, Task> activateWindow,
        Func<nint, nint, Task<bool>> selectTab,
        Func<nint, Task> refreshTab)
    {
        if (activate)
            await activateWindow(frame).ConfigureAwait(false);

        if (!await selectTab(frame, tab).ConfigureAwait(false))
            return false;

        await refreshTab(tab).ConfigureAwait(false);
        return true;
    }
}
