using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>Cheap Win32 lookups for File Explorer windows and their tab windows.</summary>
internal static class ExplorerWindows
{
    /// <summary>All top level File Explorer windows, front to back (z-order).</summary>
    public static List<nint> GetWindows()
    {
        var windows = new List<nint>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
                windows.Add(hwnd);
            return true;
        }, 0);
        return windows;
    }

    /// <summary>
    /// The tab windows of one Explorer window. The order is the child z-order - the
    /// <b>active</b> tab comes first - not the visual order of the tab strip.
    /// </summary>
    public static List<nint> GetTabs(nint windowHwnd)
    {
        var tabs = new List<nint>();
        if (windowHwnd == 0)
            return tabs;

        nint current = 0;
        while (true)
        {
            current = NativeMethods.FindWindowEx(windowHwnd, current, NativeMethods.ExplorerTabClass, null);
            if (current == 0)
                break;
            tabs.Add(current);
        }

        return tabs;
    }

    /// <summary>The tab the user is looking at (first in z-order), or 0.</summary>
    public static nint GetActiveTab(nint windowHwnd) =>
        NativeMethods.FindWindowEx(windowHwnd, 0, NativeMethods.ExplorerTabClass, null);

    /// <summary>The Explorer window that currently has focus, or 0.</summary>
    public static nint GetForegroundWindow()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
            return 0;

        if (NativeMethods.HasClassName(foreground, NativeMethods.ExplorerWindowClass))
            return foreground;

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        return NativeMethods.HasClassName(root, NativeMethods.ExplorerWindowClass) ? root : 0;
    }
}
