using System.Text;

namespace WinExplorer.Core;

/// <summary>
/// A human readable keyboard shortcut ("Ctrl+Alt+P") that can be parsed, compared and
/// rendered. Deliberately free of Win32 so it can be unit tested anywhere.
/// </summary>
public sealed record HotkeySpec
{
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Win { get; init; }

    /// <summary>Canonical key name, e.g. <c>P</c>, <c>F5</c>, <c>PageUp</c>, <c>OemComma</c>.</summary>
    public string Key { get; init; } = string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(Key);

    public static HotkeySpec Empty { get; } = new();

    public static readonly HotkeySpec DuplicateTabDefault =
        new() { Alt = true, Shift = true, Key = "D" };

    public static readonly HotkeySpec ReopenClosedDefault =
        new() { Alt = true, Shift = true, Key = "T" };

    public static readonly HotkeySpec TogglePauseDefault =
        new() { Ctrl = true, Alt = true, Key = "P" };

    public static readonly HotkeySpec NewTabHereDefault = Empty;

    /// <summary>The key names the parser accepts (canonical spelling).</summary>
    public static readonly IReadOnlyList<string> NamedKeys = new[]
    {
        "Tab", "Space", "Enter", "Escape", "Backspace", "Insert", "Delete", "Home", "End",
        "PageUp", "PageDown", "Left", "Right", "Up", "Down",
        "OemComma", "OemPeriod", "OemMinus", "OemPlus", "OemQuestion", "OemTilde",
        "OemOpenBrackets", "OemCloseBrackets", "OemPipe", "OemSemicolon", "OemQuotes"
    };

    /// <summary>
    /// Parses "ctrl + shift + t", "Alt+Shift+D", "Win+E". Returns false with a friendly
    /// message when the text is not a shortcut.
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec, out string? error)
    {
        spec = Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
            return true; // an empty shortcut is valid: "not assigned"

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Empty shortcut.";
            return false;
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        var win = false;
        string? key = null;

        foreach (var part in parts)
        {
            var token = part.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    win = true;
                    continue;
            }

            var canonical = CanonicalKeyName(token);
            if (canonical is null)
            {
                error = $"'{token}' is not a key I know.";
                return false;
            }

            if (key is not null)
            {
                error = "A shortcut can only contain one non-modifier key.";
                return false;
            }

            key = canonical;
        }

        if (key is null)
        {
            error = "A shortcut needs a key, not only modifiers.";
            return false;
        }

        if (!ctrl && !alt && !shift && !win)
        {
            error = "A shortcut needs at least one modifier (Ctrl, Alt, Shift or Win).";
            return false;
        }

        spec = new HotkeySpec { Ctrl = ctrl, Alt = alt, Shift = shift, Win = win, Key = key };
        return true;
    }

    /// <summary>Canonical spelling for a key token, or null.</summary>
    public static string? CanonicalKeyName(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (char.IsLetterOrDigit(c))
                return c.ToString();
            return null;
        }

        // F1..F24
        if (token.Length <= 3 && (token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token[1..], out var f) && f is >= 1 and <= 24)
            return "F" + f;

        foreach (var named in NamedKeys)
        {
            if (string.Equals(named, token, StringComparison.OrdinalIgnoreCase))
                return named;
        }

        return token.ToLowerInvariant() switch
        {
            "esc" => "Escape",
            "del" => "Delete",
            "ins" => "Insert",
            "pgup" => "PageUp",
            "pgdn" => "PageDown",
            "return" => "Enter",
            "comma" => "OemComma",
            "period" => "OemPeriod",
            "dot" => "OemPeriod",
            "minus" => "OemMinus",
            "slash" => "OemQuestion",
            "tilde" => "OemTilde",
            "backslash" => "OemPipe",
            _ => null
        };
    }

    public override string ToString()
    {
        if (IsEmpty)
            return "Not set";

        var builder = new StringBuilder();
        if (Ctrl) builder.Append("Ctrl+");
        if (Alt) builder.Append("Alt+");
        if (Shift) builder.Append("Shift+");
        if (Win) builder.Append("Win+");
        builder.Append(Key switch
        {
            "OemComma" => ",",
            "OemPeriod" => ".",
            "OemMinus" => "-",
            "OemPlus" => "=",
            "OemQuestion" => "/",
            "OemTilde" => "`",
            "OemOpenBrackets" => "[",
            "OemCloseBrackets" => "]",
            "OemPipe" => "\\",
            "OemSemicolon" => ";",
            "OemQuotes" => "'",
            "PageUp" => "PgUp",
            "PageDown" => "PgDn",
            _ => Key
        });
        return builder.ToString();
    }

    /// <summary>True when both shortcuts are the same chord (used for conflict checks).</summary>
    public bool SameChord(HotkeySpec other) =>
        !IsEmpty && !other.IsEmpty &&
        Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Win == other.Win &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}
