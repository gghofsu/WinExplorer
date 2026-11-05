using System.Runtime.InteropServices;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Owns the documented virtual desktop COM object on one STA. The shell and hook
/// threads each own an instance; the hide hook must never wait for the busy shell STA.
/// Unknown desktop IDs deliberately do not match anything: preserving a window is safer
/// than merging it into a different desktop when the shell is temporarily unavailable.
/// </summary>
internal sealed class VirtualDesktopManager : IDisposable
{
    private IVirtualDesktopManager? _manager;

    public VirtualDesktopManager()
    {
        try { _manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
        catch { /* Explorer may be restarting. Fail closed until reconnected. */ }
    }

    public Guid? GetWindowDesktopId(nint window)
    {
        try
        {
            if (window != 0 && _manager is not null &&
                _manager.GetWindowDesktopId(window, out var id) >= 0 && id != Guid.Empty)
                return id;
        }
        catch { /* A newly created or closing window may have no desktop yet. */ }

        return null;
    }

    public bool IsOnCurrentDesktop(nint window)
    {
        try
        {
            return window != 0 && _manager is not null &&
                   _manager.IsWindowOnCurrentVirtualDesktop(window, out var current) >= 0 && current;
        }
        catch { return false; }
    }

    public bool IsInScope(nint window, ExplorerDesktopScope scope) =>
        scope.Contains(GetWindowDesktopId(window),
            !scope.CurrentDesktopOnly || IsOnCurrentDesktop(window));

    /// <summary>
    /// Capture the caller's desktop, not the desktop of the tray/settings window. For a
    /// new Explorer registration the source frame is the authority, even if the user has
    /// switched desktops while its folder was loading.
    /// </summary>
    public ExplorerDesktopScope CaptureScope(nint sourceFrame = 0)
    {
        if (sourceFrame != 0)
            return new ExplorerDesktopScope(GetWindowDesktopId(sourceFrame));

        var foreground = NativeMethods.GetForegroundWindow();
        var id = IsOnCurrentDesktop(foreground) ? GetWindowDesktopId(foreground) : null;
        if (id is null)
        {
            // Desktop/taskbar windows can report GUID_NULL. Find a real window on the
            // current desktop instead; if it is empty, the first folder stays a window.
            NativeMethods.EnumWindows((window, _) =>
            {
                if (IsOnCurrentDesktop(window))
                    id = GetWindowDesktopId(window);
                return id is null;
            }, 0);
        }

        return new ExplorerDesktopScope(id, CurrentDesktopOnly: true);
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            try { Marshal.ReleaseComObject(_manager); }
            catch { /* Explorer may already be gone. */ }
            _manager = null;
        }
    }
}
