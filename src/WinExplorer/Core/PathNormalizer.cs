namespace WinExplorer.Core;

/// <summary>
/// Turns everything Windows can hand us for "a folder" into one canonical form so
/// two references to the same place can be compared.
/// </summary>
/// <remarks>
/// The shell speaks several dialects: plain paths (<c>C:\Users\me</c>), environment
/// variables (<c>%USERPROFILE%</c>), file URLs (<c>file:///C:/Users/me</c>) and
/// shell namespace GUIDs (<c>shell:::{20D04FE0-...}</c> for This PC). This class only
/// normalizes the *textual* form; final equality is confirmed with PIDLs by the shell
/// itself (see <c>Shell.ShellPathComparer</c>).
/// </remarks>
public static class PathNormalizer
{
    /// <summary>This PC.</summary>
    public const string ThisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>Home / Quick access.</summary>
    public const string Home = "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";

    /// <summary>Recycle bin.</summary>
    public const string RecycleBin = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>Network.</summary>
    public const string Network = "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";

    /// <summary>Control panel.</summary>
    public const string ControlPanel = "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}";

    /// <summary>
    /// Normalizes a location string. Never throws: unparsable input is returned
    /// trimmed so callers can still show it to the user.
    /// </summary>
    public static string Normalize(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return string.Empty;

        var value = location.Trim().Trim('\0');

        // Explorer hands out file URLs from LocationURL. They are decoded by hand because
        // System.Uri would treat a literal '#' in a folder name as the start of a fragment.
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            value = DecodeFileUrl(value);

        // "shell:..." and shorthand "::{GUID}" / "{GUID}"
        if (value.StartsWith("::", StringComparison.Ordinal))
            value = "shell:" + value;
        else if (value.StartsWith('{') && value.EndsWith('}'))
            value = "shell:::" + value;

        // Expand %VARS% only when present; ExpandEnvironmentVariables is cheap but
        // leaves unknown variables untouched.
        if (value.Contains('%'))
        {
            try { value = Environment.ExpandEnvironmentVariables(value); }
            catch { /* keep original */ }
        }

        // Explorer accepts forward slashes; use backslashes so the fast textual
        // comparison below treats "C:/Temp/" and "C:\Temp" as equal.
        value = value.Replace('/', '\\');

        // Keep a single trailing separator for drive roots ("C:\"), trim others.
        if (value.Length > 3 && (value.EndsWith('\\') || value.EndsWith('/')))
            value = value.TrimEnd('\\', '/');

        value = value.Trim('"', '\'', ' ');

        return value;
    }

    /// <summary>
    /// True when the normalized location names a shell namespace folder
    /// (This PC, Control Panel, ...) rather than something on disk.
    /// </summary>
    public static bool IsShellNamespace(string normalizedLocation) =>
        normalizedLocation.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        normalizedLocation.StartsWith("::{", StringComparison.Ordinal);

    /// <summary>Formats a location for the UI (last segment, or a friendly shell name).</summary>
    public static string ToDisplayName(string? location)
    {
        var value = Normalize(location);
        if (value.Length == 0)
            return "File Explorer";

        if (IsShellNamespace(value))
        {
            if (StartsWithIgnoreCase(value, ThisPc)) return "This PC";
            if (StartsWithIgnoreCase(value, Home)) return "Home";
            if (StartsWithIgnoreCase(value, RecycleBin)) return "Recycle Bin";
            if (StartsWithIgnoreCase(value, Network)) return "Network";
            if (StartsWithIgnoreCase(value, ControlPanel)) return "Control Panel";
            return "Shell folder";
        }

        var trimmed = value.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed.ToUpperInvariant();

        var index = trimmed.LastIndexOf('\\');
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }

    /// <summary>Decodes <c>file:///C:/a%20b</c> into <c>C:\a b</c>.</summary>
    internal static string DecodeFileUrl(string url)
    {
        var rest = url["file://".Length..];

        // "file:///C:/x" has an empty host; "file://server/share" is a UNC path.
        var unc = false;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            unc = true;
        }
        else if (rest.StartsWith('/'))
        {
            rest = rest[1..];
        }
        else if (rest.Length > 0)
        {
            unc = true;
        }

        string decoded;
        try
        {
            // UnescapeDataString decodes %20 and multi byte (UTF-8) escapes, and leaves
            // invalid sequences alone instead of throwing.
            decoded = Uri.UnescapeDataString(rest);
        }
        catch (UriFormatException)
        {
            decoded = rest;
        }

        decoded = decoded.Replace('/', '\\');
        return unc ? "\\\\" + decoded : decoded;
    }

    private static bool StartsWithIgnoreCase(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
