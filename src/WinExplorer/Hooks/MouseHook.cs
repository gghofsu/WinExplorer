using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;
using WinExplorer.Services;
using WinExplorer.Shell;

namespace WinExplorer.Hooks;

/// <summary>What kind of click requested a folder open.</summary>
internal enum FolderClickKind
{
    DoubleClick,
    MiddleClick
}

internal sealed record FolderClick(
    FolderClickKind Kind,
    nint Window,
    nint Tab,
    int X,
    int Y,
    string? ItemName,
    int Role);

/// <summary>
/// Intercepts double-clicks and middle-clicks on folders *inside* a File Explorer window
/// and turns them into "open as a tab" requests.
/// </summary>
/// <remarks>
/// The second button-down of a double-click is swallowed so Explorer never starts its own
/// navigation. The folder is then resolved in the background: if it turns out the click
/// hit something that is not a folder (a file, a virtual item, ...) the click is replayed
/// with <c>SendInput</c> so Windows does exactly what it would have done.
/// </remarks>
internal sealed class MouseHook : IDisposable
{
    private readonly Logger _log;
    private readonly Func<AppSettings> _settings;
    private readonly Func<FolderClick, Task> _handler;
    private readonly NativeMethods.HookProc _callback;
    private nint _hook;
    private bool _disposed;

    private int _lastDownTick;
    private int _lastDownX;
    private int _lastDownY;
    private bool _blockLeftUp;
    private bool _blockMiddleUp;

    private static long _lastWindowGestureTick;

    // The folder row that was right-clicked last inside a File Explorer file list. This is
    // the only right-click that can mean "Open in new window", so it is the only one that
    // may protect the next window from being folded into a tab.
    private static readonly object _folderRightGate = new();
    private static long _lastFolderRightUpTick;
    private static string _lastFolderRightName = string.Empty;

    public MouseHook(Logger log, Func<AppSettings> settings, Func<FolderClick, Task> handler)
    {
        _log = log;
        _settings = settings;
        _handler = handler;
        _callback = HookCallback;
    }

    public bool IsRunning => _hook != 0;

    /// <summary>
    /// True when the user recently asked for a separate window: Shift+double-click (or any
    /// other modifier + double-click, which Windows handles itself). Windows opened during
    /// this window are left alone by the merger, and are pinned afterwards.
    /// </summary>
    public static bool NewWindowGestureRecently(int ms = 3000) =>
        _lastWindowGestureTick != 0 &&
        Environment.TickCount64 - Interlocked.Read(ref _lastWindowGestureTick) < ms;

    /// <summary>Records "the user wants a window" (used when a gesture is replayed).</summary>
    public static void NoteNewWindowGesture() =>
        Interlocked.Exchange(ref _lastWindowGestureTick, Environment.TickCount64);

    /// <summary>
    /// True when a folder row in a File Explorer file list was right-clicked recently. Used to
    /// hold back the auto-folder for a moment: the context menu may still be open.
    /// </summary>
    public static bool FolderRightClickRecently(int ms = 2500)
    {
        lock (_folderRightGate)
        {
            return _lastFolderRightUpTick != 0 && Environment.TickCount64 - _lastFolderRightUpTick < ms;
        }
    }

    /// <summary>
    /// True when <paramref name="location"/> is the folder that was right-clicked recently,
    /// which is what "Open in new window" produces. Any other right-click (a file, the
    /// desktop, another app, empty space) does not count, so a stray right-click can no
    /// longer stop a window from being folded into a tab.
    /// </summary>
    public static bool FolderRightClickMatches(string? location, int ms = 20000)
    {
        lock (_folderRightGate)
        {
            if (_lastFolderRightUpTick == 0 ||
                Environment.TickCount64 - _lastFolderRightUpTick >= ms ||
                _lastFolderRightName.Length == 0)
            {
                return false;
            }

            return string.Equals(PathNormalizer.ToDisplayName(location), _lastFolderRightName,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Remembers a right-click, but only when it landed on a folder row. The accessibility
    /// lookup runs on the thread pool: a hook callback must never wait for Explorer.
    /// </summary>
    private void RememberFolderRightClick(POINT point)
    {
        try
        {
            var hwnd = NativeMethods.WindowFromPoint(point);
            if (hwnd == 0 || !AccessiblePoint.IsInsideFolderList(hwnd))
                return;

            var element = AccessiblePoint.Describe(point.X, point.Y);
            if (element is null || element.Role != MsaaRole.ListItem || !element.IsFolderLikeItem)
                return;

            lock (_folderRightGate)
            {
                _lastFolderRightUpTick = Environment.TickCount64;
                _lastFolderRightName = element.Name ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Right-click probe failed: {ex.Message}");
        }
    }

    public void Start()
    {
        if (_hook != 0)
            return;

        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback,
            NativeMethods.GetModuleHandle(null), 0);

        if (_hook == 0)
            _log.Warn($"Could not install the mouse hook (error {Marshal.GetLastWin32Error()}).");
        else
            _log.Info("Mouse hook installed.");
    }

    public void Stop()
    {
        if (_hook == 0)
            return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _log.Info("Mouse hook removed.");
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        var message = (uint)wParam.ToInt64();

        try
        {
            switch (message)
            {
                case NativeMethods.WM_RBUTTONUP:
                {
                    var rightPoint = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
                    ThreadPool.QueueUserWorkItem(_ => RememberFolderRightClick(rightPoint));
                    break;
                }

                case NativeMethods.WM_LBUTTONDOWN:
                    if (OnLeftButtonDown(lParam))
                    {
                        _blockLeftUp = true;
                        return 1;
                    }
                    break;

                case NativeMethods.WM_LBUTTONUP:
                    if (_blockLeftUp)
                    {
                        _blockLeftUp = false;
                        return 1;
                    }
                    break;

                case NativeMethods.WM_MBUTTONDOWN:
                {
                    var settings = _settings();
                    if (!settings.Enabled || !settings.InterceptMiddleClick)
                        break;

                    var click = Resolve(FolderClickKind.MiddleClick, lParam);
                    if (click is null)
                        break;

                    _blockMiddleUp = true;
                    Dispatch(click);
                    return 1;
                }

                case NativeMethods.WM_MBUTTONUP:
                    if (_blockMiddleUp)
                    {
                        _blockMiddleUp = false;
                        return 1;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Mouse hook error", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool OnLeftButtonDown(nint lParam)
    {
        var settings = _settings();
        if (!settings.Enabled || !settings.InterceptDoubleClick)
            return false;

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0)
            return false;

        var now = Environment.TickCount;
        var isSecondClick = _lastDownTick != 0 &&
                            now - _lastDownTick <= NativeMethods.GetDoubleClickTime() &&
                            Math.Abs(data.pt.X - _lastDownX) <= NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK) &&
                            Math.Abs(data.pt.Y - _lastDownY) <= NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK);

        if (!isSecondClick)
        {
            _lastDownTick = now;
            _lastDownX = data.pt.X;
            _lastDownY = data.pt.Y;
            return false;
        }

        _lastDownTick = 0;

        // Shift+double-click (and Ctrl/Alt/Win+double-click) is the escape hatch: Windows
        // opens the folder in its own window. Remember it so the watcher does not fold that
        // window straight back into a tab.
        if (NativeMethods.AnyModifierDown())
        {
            NoteNewWindowGesture();
            _log.Debug("Modified double-click: leaving the window to Windows.");
            return false;
        }

        var click = Resolve(FolderClickKind.DoubleClick, lParam);
        if (click is null)
            return false;

        Dispatch(click);
        return true;
    }

    private void Dispatch(FolderClick click)
    {
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                await _handler(click).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Handling a folder click failed", ex);
            }
        });
    }

    /// <summary>
    /// Works out whether the click landed on a folder row. Runs inside the hook, so it only
    /// performs cheap window checks plus one MSAA query.
    /// </summary>
    private FolderClick? Resolve(FolderClickKind kind, nint lParam)
    {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

        var hwnd = NativeMethods.WindowFromPoint(data.pt);
        if (hwnd == 0)
            return null;

        if (!AccessiblePoint.IsInsideFileList(hwnd))
            return null;

        var (tab, window) = ExplorerWindows.GetTabAndWindowFromChild(hwnd);
        if (window == 0 || tab == 0)
            return null;

        if (NativeMethods.GetWindowThreadProcessId(window, out _) == 0)
            return null;

        var element = AccessiblePoint.Describe(data.pt.X, data.pt.Y);
        if (element is null || !element.IsFolderLikeItem)
            return null;

        // Only rows of the file list; the navigation pane reports tree items.
        if (element.Role != MsaaRole.ListItem)
            return null;

        return new FolderClick(kind, window, tab, data.pt.X, data.pt.Y, element.Name, element.Role);
    }

    /// <summary>
    /// Replays the click the user made, for items that turned out not to be folders.
    /// Injected clicks carry <c>LLMHF_INJECTED</c>, so the hook ignores them.
    /// </summary>
    public static void ReplayDoubleClick(int x, int y)
    {
        if (!NativeMethods.SetCursorPos(x, y))
            return;

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        Thread.Sleep(30);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    public static void ReplayMiddleClick(int x, int y)
    {
        if (!NativeMethods.SetCursorPos(x, y))
            return;

        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
