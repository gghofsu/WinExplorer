using System.Windows.Input;
using WinExplorer.Core;

namespace WinExplorer.UI;

/// <summary>Converts WPF key events into <see cref="HotkeySpec"/> chords.</summary>
public static class WpfKeyMap
{
    /// <summary>Builds a shortcut from the pressed key and the current modifier state.</summary>
    public static HotkeySpec? FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Modifier keys alone cannot form a shortcut.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return null;
        }

        var canonical = ToCanonical(key);
        if (canonical is null)
            return null;

        var spec = new HotkeySpec
        {
            Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            Shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift),
            Win = Keyboard.Modifiers.HasFlag(ModifierKeys.Windows),
            Key = canonical
        };

        return spec;
    }

    private static string? ToCanonical(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return ((char)('A' + (key - Key.A))).ToString();

        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();

        if (key is >= Key.F1 and <= Key.F24)
            return "F" + (key - Key.F1 + 1);

        return key switch
        {
            Key.Tab => "Tab",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.OemComma => "OemComma",
            Key.OemPeriod => "OemPeriod",
            Key.OemMinus => "OemMinus",
            Key.OemPlus => "OemPlus",
            Key.OemQuestion => "OemQuestion",
            Key.OemTilde => "OemTilde",
            Key.OemOpenBrackets => "OemOpenBrackets",
            Key.OemCloseBrackets => "OemCloseBrackets",
            Key.OemPipe => "OemPipe",
            Key.OemSemicolon => "OemSemicolon",
            Key.OemQuotes => "OemQuotes",
            _ => null
        };
    }
}
