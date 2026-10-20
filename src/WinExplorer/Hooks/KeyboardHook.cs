using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;
using WinExplorer.Services;

namespace WinExplorer.Hooks;

public enum HotkeyAction
{
    DuplicateTab,
    ReopenClosedTab,
    TogglePause,
    NewTabHere
}

/// <summary>
/// Global shortcuts, implemented with a low level keyboard hook so the keys never reach
/// Explorer (and so they work when a different app has focus, for the global toggle).
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Logger _log;
    private readonly Func<AppSettings> _settings;
    private readonly Action<HotkeyAction> _handler;
    private readonly NativeMethods.HookProc _callback;
    private nint _hook;
    private bool _disposed;
    private int _lastHandledTick;
    private int _lastHandledKey;

    private static readonly Dictionary<int, string> VirtualKeyNames = BuildKeyNames();

    public KeyboardHook(Logger log, Func<AppSettings> settings, Action<HotkeyAction> handler)
    {
        _log = log;
        _settings = settings;
        _handler = handler;
        _callback = HookCallback;
    }

    public bool IsRunning => _hook != 0;

    public void Start()
    {
        if (_hook != 0)
            return;

        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback,
            NativeMethods.GetModuleHandle(null), 0);

        if (_hook == 0)
            _log.Warn($"Could not install the keyboard hook (error {Marshal.GetLastWin32Error()}).");
        else
            _log.Info("Keyboard hook installed.");
    }

    public void Stop()
    {
        if (_hook == 0)
            return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;
        _log.Info("Keyboard hook removed.");
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        var message = (uint)wParam.ToInt64();
        if (message is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN))
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0)
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

            var settings = _settings();
            var (duplicate, reopen, toggle) = settings.GetHotkeys();
            var key = (int)data.vkCode;

            // Ignore auto-repeat.
            var now = Environment.TickCount;
            if (key == _lastHandledKey && now - _lastHandledTick < 500)
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

            if (Matches(toggle, key))
            {
                _lastHandledKey = key;
                _lastHandledTick = now;
                _handler(HotkeyAction.TogglePause);
                return 1;
            }

            if (!settings.Enabled)
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

            if (Matches(duplicate, key))
            {
                _lastHandledKey = key;
                _lastHandledTick = now;
                _handler(HotkeyAction.DuplicateTab);
                return 1;
            }

            if (Matches(reopen, key))
            {
                _lastHandledKey = key;
                _lastHandledTick = now;
                _handler(HotkeyAction.ReopenClosedTab);
                return 1;
            }

            // Optional: take over Win+E so it opens a tab instead of a window.
            if (settings.TakeOverWinE && data.vkCode == 0x45 /* E */ &&
                NativeMethods.IsWinDown() && !NativeMethods.IsCtrlDown() && !NativeMethods.IsAltDown())
            {
                _lastHandledKey = key;
                _lastHandledTick = now;
                _handler(HotkeyAction.NewTabHere);
                return 1;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Keyboard hook error", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Exact modifier match: Ctrl+Alt+P must not fire for Ctrl+Alt+Shift+P.</summary>
    private static bool Matches(HotkeySpec spec, int virtualKey)
    {
        if (spec.IsEmpty)
            return false;

        if (!VirtualKeyNames.TryGetValue(virtualKey, out var name) ||
            !string.Equals(name, spec.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NativeMethods.IsCtrlDown() == spec.Ctrl &&
               NativeMethods.IsAltDown() == spec.Alt &&
               NativeMethods.IsShiftDown() == spec.Shift &&
               NativeMethods.IsWinDown() == spec.Win;
    }

    private static Dictionary<int, string> BuildKeyNames()
    {
        var map = new Dictionary<int, string>();

        for (var c = 'A'; c <= 'Z'; c++)
            map[(int)c] = c.ToString();

        for (var d = '0'; d <= '9'; d++)
            map[(int)d] = d.ToString();

        for (var f = 1; f <= 24; f++)
            map[0x70 + f - 1] = "F" + f;

        map[0x09] = "Tab";
        map[0x20] = "Space";
        map[0x0D] = "Enter";
        map[0x1B] = "Escape";
        map[0x08] = "Backspace";
        map[0x2D] = "Insert";
        map[0x2E] = "Delete";
        map[0x24] = "Home";
        map[0x23] = "End";
        map[0x21] = "PageUp";
        map[0x22] = "PageDown";
        map[0x25] = "Left";
        map[0x26] = "Up";
        map[0x27] = "Right";
        map[0x28] = "Down";
        map[0xBC] = "OemComma";
        map[0xBE] = "OemPeriod";
        map[0xBD] = "OemMinus";
        map[0xBB] = "OemPlus";
        map[0xBF] = "OemQuestion";
        map[0xC0] = "OemTilde";
        map[0xDB] = "OemOpenBrackets";
        map[0xDD] = "OemCloseBrackets";
        map[0xDC] = "OemPipe";
        map[0xBA] = "OemSemicolon";
        map[0xDE] = "OemQuotes";

        return map;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
