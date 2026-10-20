using System.Collections.Concurrent;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Makes a freshly created Explorer window invisible for the split second it takes to
/// fold it into a tab, so the user never sees it flash on screen.
/// </summary>
internal static class WindowAnimator
{
    private static readonly ConcurrentDictionary<nint, bool> Hidden = new();

    public static bool IsHidden(nint hwnd) => Hidden.ContainsKey(hwnd);

    /// <summary>Fades the window to fully transparent. Returns false if it could not be hidden.</summary>
    public static bool Hide(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return false;

        try
        {
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((style & NativeMethods.WS_EX_LAYERED) == 0)
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_LAYERED);

            if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 0, NativeMethods.LWA_ALPHA))
                return false;

            Hidden[hwnd] = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores a window hidden by <see cref="Hide"/>.</summary>
    public static void Show(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return;

        try
        {
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style & ~NativeMethods.WS_EX_LAYERED);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Hidden.TryRemove(hwnd, out _);
        }
    }

    /// <summary>Brings a window to the front, restoring it first if it is minimized.</summary>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            return false;

        if (Hidden.ContainsKey(hwnd))
            Show(hwnd);

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);

        if (NativeMethods.GetForegroundWindow() == hwnd)
            return true;

        if (NativeMethods.SetForegroundWindow(hwnd))
            return true;

        // The foreground lock blocks background processes. A harmless synthetic key
        // press plus a temporary AttachThreadInput usually gets us through.
        NativeMethods.BypassForegroundLock();

        if (NativeMethods.SetForegroundWindow(hwnd))
            return true;

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

        var attached = false;
        try
        {
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
                attached = NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);

            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }
}
