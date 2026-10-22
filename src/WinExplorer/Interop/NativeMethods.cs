using System.Runtime.InteropServices;
using System.Text;

namespace WinExplorer.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHELLEXECUTEINFO
{
    public int cbSize;
    public uint fMask;
    public nint hwnd;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
    public int nShow;
    public nint hInstApp;
    public nint lpIDList;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
    public nint hkeyClass;
    public uint dwHotKey;
    public nint hIcon;
    public nint hProcess;
}

/// <summary>Every P/Invoke used by WinExplorer, in one place.</summary>
internal static class NativeMethods
{
    // ---- Window classes -------------------------------------------------
    internal const string ExplorerWindowClass = "CabinetWClass";
    internal const string ExplorerTabClass = "ShellTabWindowClass";
    internal const string TaskbarClass = "Shell_TrayWnd";

    // ---- Explorer tab commands (undocumented WM_COMMAND ids) ------------
    /// <summary>Same as Ctrl+T: add a tab to the window that owns the tab window.</summary>
    internal const int ExplorerCommandNewTab = 0xA21B;

    /// <summary>Same as Ctrl+W: close the tab.</summary>
    internal const int ExplorerCommandCloseTab = 0xA021;

    /// <summary>Same as Ctrl+1..n: wParam = 0xA221, lParam = 1-based tab index.</summary>
    internal const int ExplorerCommandSelectTab = 0xA221;

    // ---- Messages -------------------------------------------------------
    internal const uint WM_NULL = 0x0000;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_DROPFILES = 0x0233;

    internal const int SMTO_ABORTIFHUNG = 0x0002;
    internal const int SMTO_BLOCK = 0x0001;
    internal const int SMTO_ERRORONEXIT = 0x0020;

    // ---- ShowWindow -----------------------------------------------------
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWMINIMIZED = 2;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_RESTORE = 9;

    // ---- SetWindowPos ---------------------------------------------------
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;

    // ---- Window styles --------------------------------------------------
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint LWA_ALPHA = 0x2;

    internal const int GWL_STYLE = -16;
    internal const int WS_MAXIMIZE = 0x01000000;

    // ---- GetAncestor ----------------------------------------------------
    internal const uint GA_PARENT = 1;
    internal const uint GA_ROOT = 2;
    internal const uint GA_ROOTOWNER = 3;

    // ---- Hooks ----------------------------------------------------------
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int HC_ACTION = 0;

    internal const uint EVENT_OBJECT_CREATE = 0x8000;
    internal const uint EVENT_OBJECT_DESTROY = 0x8001;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;
    internal const uint EVENT_OBJECT_HIDE = 0x8003;
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    internal const uint LLKHF_INJECTED = 0x10;

    // ---- Virtual keys ---------------------------------------------------
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal const int VK_F23 = 0x86;
    internal const int VK_ESCAPE = 0x1B;

    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // ---- Shell ----------------------------------------------------------
    internal const uint SIGDN_URL = 0x80068000;
    internal const uint SIGDN_FILESYSPATH = 0x80058000;
    internal const uint SHCIDS_CANONICALONLY = 0x10000000;

    internal const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    internal const uint SEE_MASK_NOASYNC = 0x00000100;
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOZONECHECKS = 0x00800000;

    internal const int ASFW_ANY = -1;

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time);

    // ---- user32 ---------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string? className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam,
        int flags, int timeoutMs, out nint result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(nint hwnd, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern int GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal const int SM_CXDOUBLECLK = 36;
    internal const int SM_CYDOUBLECLK = 37;
    internal const int SM_SWAPBUTTON = 23;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern void mouse_event(uint flags, int dx, int dy, uint data, nint extraInfo);

    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nint extraInfo);

    // ---- Hooks ----------------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int hookId, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out MSG message, nint hwnd, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out MSG message, nint hwnd, uint minFilter, uint maxFilter, uint removeMsg);

    [DllImport("user32.dll")]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[]? handles, uint milliseconds,
        uint wakeMask, uint flags);

    internal const uint PM_REMOVE = 0x0001;
    internal const uint PM_NOREMOVE = 0x0000;
    internal const uint QS_ALLINPUT = 0x04FF;
    internal const uint MWMO_INPUTAVAILABLE = 0x0004;

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? moduleName);

    // ---- Win events -----------------------------------------------------
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    // ---- shell32 --------------------------------------------------------
    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHGetDesktopFolder(out nint desktopFolder);

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHGetNameFromIDList(nint pidl, uint sigdnName, out nint name);

    [DllImport("shell32.dll")]
    internal static extern nint ILCombine(nint pidl1, nint pidl2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributes, out uint eaten);

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SHOpenFolderAndSelectItems(nint folderPidl, uint count, nint[]? items, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    // ---- ole32 ----------------------------------------------------------
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    internal const uint COINIT_APARTMENTTHREADED = 0x2;
    internal const uint COINIT_DISABLE_OLE1DDE = 0x4;

    // ---- oleacc ---------------------------------------------------------
    // The last parameter is a VARIANT (not a handle): marshal it as a boxed object.
    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromPoint(POINT point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible,
        [Out] out object? childId);

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromWindow(nint hwnd, uint objectId,
        [In] ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    // ---- shell known folders ---------------------------------------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token,
        out nint path);

    internal const uint OBJID_CLIENT = 0xFFFFFFFC;
    internal const uint OBJID_WINDOW = 0x00000000;

    // ---- kernel32 -------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- Helpers --------------------------------------------------------

    internal static string GetClassName(nint hwnd)
    {
        if (hwnd == 0)
            return string.Empty;

        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    internal static bool HasClassName(nint hwnd, string className) =>
        string.Equals(GetClassName(hwnd), className, StringComparison.OrdinalIgnoreCase);

    internal static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    internal static bool IsCtrlDown() => IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
    internal static bool IsShiftDown() => IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT);
    internal static bool IsAltDown() => IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);
    internal static bool IsWinDown() => IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
    internal static bool AnyModifierDown() => IsCtrlDown() || IsShiftDown() || IsAltDown() || IsWinDown();

    /// <summary>Reads the file name of a process (used to prove the window belongs to explorer.exe).</summary>
    internal static string? GetProcessPath(uint processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
            return null;

        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    internal static string? GetNameFromPidl(nint pidl, uint sigdn = SIGDN_URL)
    {
        if (pidl == 0)
            return null;

        if (SHGetNameFromIDList(pidl, sigdn, out var ptr) != 0 || ptr == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>Nudges Windows into letting us call SetForegroundWindow.</summary>
    internal static void BypassForegroundLock()
    {
        // A synthetic key press is the documented workaround for the foreground lock:
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setforegroundwindow
        keybd_event((byte)VK_F23, 0, 0, 0);
        keybd_event((byte)VK_F23, 0, KEYEVENTF_KEYUP, 0);
    }
}
