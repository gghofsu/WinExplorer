using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Reads the folder a File Explorer window/tab shows from its own UI (the address bar),
/// without asking the shell automation server anything.
/// </summary>
/// <remarks>
/// This is the second opinion when <see cref="ShellCom"/> comes back empty: every Explorer
/// window exposes the breadcrumb as an MSAA element (<c>ROLE_SYSTEM_COMBOBOX</c>, or an
/// <c>ROLE_SYSTEM_TEXT</c> edit while the address bar is being typed into) whose name or value
/// is the full path. Must run on the shell STA thread, like every other MSAA call.
/// </remarks>
internal static class AddressBarText
{
    private const int RoleText = 0x2A;
    private const int RoleComboBox = 0x2E;
    private const int RoleClient = 0x0A;
    private const int RolePane = 0x10;
    private const int RoleDocument = 0x0F;
    private const int RoleWindow = 0x09;

    /// <summary>The location shown by <paramref name="windowHwnd"/>, or null.</summary>
    public static string? TryRead(nint windowHwnd)
    {
        if (windowHwnd == 0)
            return null;

        IAccessible? root = null;
        try
        {
            if (FromWindow(windowHwnd, out root) && root is not null)
            {
                var found = Walk(root, depth: 0, budget: 400);
                if (found is not null)
                    return found;
            }
        }
        catch
        {
            // MSAA is best effort; a failure just means "no second opinion".
        }
        finally
        {
            Release(root);
        }

        return null;
    }

    private static bool FromWindow(nint hwnd, out IAccessible? accessible)
    {
        accessible = null;

        var iid = typeof(IAccessible).GUID;
        var hr = NativeMethods.AccessibleObjectFromWindow(hwnd, NativeMethods.OBJID_CLIENT, ref iid, out accessible);
        if (hr == 0 && accessible is not null)
            return true;

        accessible = null;
        hr = NativeMethods.AccessibleObjectFromWindow(hwnd, NativeMethods.OBJID_WINDOW, ref iid, out accessible);
        return hr == 0 && accessible is not null;
    }

    /// <summary>
    /// Looks for an element whose text is a location. The file list itself is skipped on
    /// purpose: it holds thousands of items and none of them is the current folder.
    /// </summary>
    private static string? Walk(IAccessible accessible, int depth, int budget)
    {
        if (depth > 3 || budget <= 0)
            return null;

        var count = 0;
        try
        {
            count = accessible.get_accChildCount();
        }
        catch
        {
            return null;
        }

        if (count <= 0 || count > 400)
            return null;

        var index = 0;
        for (var i = 1; i <= count; i++)
        {
            index++;
            if (index > budget)
                return null;

            object? child = null;
            try
            {
                child = accessible.get_accChild(Child(i));

                // Simple children have no object of their own: ask the parent by index.
                var role = ChildRole(accessible, child, i);
                if (role == MsaaRole.List || role == MsaaRole.Outline)
                    continue; // the file list / navigation pane

                var text = role is RoleComboBox or RoleText || role == RoleDocument || role == RolePane
                    ? TextOf(accessible, child, i)
                    : null;

                var location = NormalizeLocation(text);
                if (location is not null)
                    return location;

                if (child is IAccessible nested)
                {
                    var nestedLocation = Walk(nested, depth + 1, budget - index);
                    if (nestedLocation is not null)
                        return nestedLocation;
                }
            }
            catch
            {
                // Skip this element.
            }
            finally
            {
                Release(child);
            }
        }

        return null;
    }

    private static int ChildRole(IAccessible parent, object? child, int index)
    {
        try
        {
            var value = child is IAccessible nested
                ? nested.get_accRole(null)
                : parent.get_accRole(Child(index));
            return ToInt(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string? TextOf(IAccessible parent, object? child, int index)
    {
        string? name = null;
        string? value = null;

        try
        {
            value = child is IAccessible nested ? nested.get_accValue(null) : parent.get_accValue(Child(index));
        }
        catch
        {
            // ignore
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            try
            {
                name = child is IAccessible nested ? nested.get_accName(null) : parent.get_accName(Child(index));
            }
            catch
            {
                // ignore
            }
        }

        return string.IsNullOrWhiteSpace(value) ? name : value;
    }

    /// <summary>Accepts only text that really is a folder location.</summary>
    private static string? NormalizeLocation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var candidate = text.Trim();
        if (candidate.Length < 3 || candidate.Length > 32767)
            return null;

        // Breadcrumb items ("Users", "Documents") and tooltips ("3 items") are not locations.
        var looksLikePath = candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                            candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                            candidate.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                            candidate.StartsWith("::", StringComparison.Ordinal) ||
                            (candidate.Length > 2 && char.IsLetter(candidate[0]) && candidate[1] == ':');

        if (!looksLikePath)
            return null;

        var normalized = PathNormalizer.Normalize(candidate);
        return normalized.Length == 0 ? null : normalized;
    }

    private static object Child(int index) => index;

    private static int ToInt(object? value)
    {
        try
        {
            if (value is int number)
                return number;

            // MSAA returns a VARIANT; a role is normally a VT_I4.
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;

        try { Marshal.ReleaseComObject(instance); } catch { /* ignore */ }
    }
}
