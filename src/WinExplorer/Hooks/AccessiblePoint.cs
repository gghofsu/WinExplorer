using System.Globalization;
using System.Runtime.InteropServices;
using WinExplorer.Interop;

namespace WinExplorer.Hooks;

/// <summary>A UI element found under the cursor.</summary>
internal sealed record PointElement(nint Hwnd, int Role, string? Name, string? Value)
{
    public bool IsFolderLikeItem => MsaaRole.IsItem(Role) && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>
/// Answers "what is under the mouse?" using MSAA (Microsoft Active Accessibility), the same
/// information screen readers get. Explorer exposes every file/folder row as a list item
/// whose <c>accName</c> is the item's display name.
/// </summary>
internal static class AccessiblePoint
{
    /// <summary>Looks up the accessible object under a screen point. Never throws.</summary>
    public static PointElement? Describe(int x, int y)
    {
        IAccessible? accessible = null;
        try
        {
            var point = new POINT(x, y);
            var hr = NativeMethods.AccessibleObjectFromPoint(point, out var acc, out var childId);
            if (hr != 0 || acc is null)
                return null;

            accessible = acc;

            // childId is a VARIANT: 0 (CHILDID_SELF) means the object itself, any other
            // value is the 1-based index of the child inside it.
            object child = childId ?? 0;

            var role = ReadRole(acc, child);
            var name = SafeName(acc, child);
            var value = SafeValue(acc, child);

            return new PointElement(NativeMethods.WindowFromPoint(point), role, name, value);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (accessible is not null)
            {
                try { Marshal.ReleaseComObject(accessible); }
                catch { /* ignore */ }
            }
        }
    }

    private static int ReadRole(IAccessible accessible, object child)
    {
        try
        {
            var role = accessible.get_accRole(child);
            return role switch
            {
                int value => value,
                short value => value,
                null => -1,
                _ => Convert.ToInt32(role, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return -1;
        }
    }

    private static string? SafeName(IAccessible accessible, object child)
    {
        try { return accessible.get_accName(child); }
        catch { return null; }
    }

    private static string? SafeValue(IAccessible accessible, object child)
    {
        try { return accessible.get_accValue(child); }
        catch { return null; }
    }

    /// <summary>
    /// True when the window under the cursor is inside the file list of an Explorer tab
    /// (and not in the navigation pane, the address bar, the command bar, ...).
    /// This is a pure window-hierarchy check, so it is cheap enough to run inside a hook.
    /// </summary>
    public static bool IsInsideFileList(nint hwnd)
    {
        if (hwnd == 0)
            return false;

        var sawView = false;
        var current = hwnd;

        for (var depth = 0; depth < 16 && current != 0; depth++)
        {
            var className = NativeMethods.GetClassName(current);

            // Chrome around the file list: the user is clicking a control, not an item.
            if (className.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ToolbarWindow32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ReBarWindow32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("msvb_lib_toolbar", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ComboBox", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ComboBoxEx32", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (className.Equals("DirectUIHWND", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SysListView32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
            {
                sawView = true;
            }

            if (className.Equals(NativeMethods.ExplorerTabClass, StringComparison.OrdinalIgnoreCase))
                return sawView;

            current = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
        }

        return false;
    }
}
