using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Reads the Explorer windows/tabs through <c>CLSID_ShellWindows</c> - the same object
/// <c>Shell.Application.Windows</c> returns, but created and called directly.
/// </summary>
/// <remarks>
/// This is the fallback for the real case where <c>Shell.Application</c> hands out a
/// <c>Windows</c> collection that turns out to be empty: on such builds the late-bound
/// property either fails or returns nothing, while the underlying shell windows object is
/// healthy - it is what Explorer itself uses to track its tabs.
///
/// The typed <see cref="IShellWindows"/> interface is used when the cast succeeds; otherwise
/// the same object is driven through late binding, so a type-library mismatch only costs
/// speed, not the feature.
/// </remarks>
internal static class ShellWindowsQuery
{
    /// <summary>Tab handle -> location, exactly like <see cref="ShellCom.QueryTabLocations"/>.</summary>
    public static Dictionary<nint, ShellTabLocation> Query(out string error)
    {
        var result = new Dictionary<nint, ShellTabLocation>();
        error = string.Empty;

        object? raw = null;
        try
        {
            raw = new ShellWindowsClass();

            var typed = raw as IShellWindows;
            var count = 0;

            if (typed is not null)
            {
                var hr = typed.get_Count(out count);
                if (hr != 0)
                {
                    error = $"IShellWindows.Count failed with 0x{hr:X8}";
                    return result;
                }
            }
            else
            {
                count = ToInt(Read(raw, "Count", out var countError));
                if (count == 0)
                {
                    error = $"ShellWindows.Count: {countError ?? "0"}";
                    return result;
                }
            }

            if (count == 0)
                error = "ShellWindows reports no window at all";

            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    string? itemError = null;
                    if (typed is not null)
                    {
                        var hr = typed.Item(i, out window);
                        if (hr != 0)
                            itemError = $"0x{hr:X8}";
                    }
                    else
                    {
                        window = Read(raw, "Item", out itemError, i);
                    }

                    if (window is null)
                    {
                        if (error.Length == 0)
                            error = $"ShellWindows.Item({i}) returned nothing ({itemError ?? "no error"})";
                        continue;
                    }

                    var hwnd = (nint)ToLong(Read(window, "HWND", out var hwndError));
                    if (hwnd == 0)
                    {
                        if (error.Length == 0)
                            error = $"ShellWindows.Item({i}) has no HWND ({hwndError ?? "no error"})";
                        continue;
                    }

                    var location = ReadLocation(window);
                    var name = Read(window, "LocationName", out _) as string
                               ?? PathNormalizer.ToDisplayName(location);

                    result[hwnd] = new ShellTabLocation(hwnd, ParentWindowOf(hwnd),
                        PathNormalizer.Normalize(location), name);
                }
                catch (Exception ex)
                {
                    if (error.Length == 0)
                        error = $"ShellWindows.Item({i}): {Describe(ex)}";
                }
                finally
                {
                    Release(window);
                }
            }
        }
        catch (Exception ex)
        {
            error = $"CLSID_ShellWindows: {Describe(ex)}";
        }
        finally
        {
            Release(raw);
        }

        return result;
    }

    /// <summary>Reads one window's properties directly, for --probe.</summary>
    public static string ProbePropertyErrors(nint windowHwnd)
    {
        object? raw = null;
        try
        {
            raw = new ShellWindowsClass();
            var typed = raw as IShellWindows;
            var count = typed is not null
                ? (typed.get_Count(out var c) == 0 ? c : 0)
                : ToInt(Read(raw, "Count", out _));

            for (var i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    if (typed is not null)
                        typed.Item(i, out window);
                    else
                        window = Read(raw, "Item", out _, i);

                    if (window is null)
                        continue;

                    if ((nint)ToLong(Read(window, "HWND", out _)) != windowHwnd)
                        continue;

                    var url = Read(window, "LocationURL", out var urlError);
                    if (urlError is not null)
                        return $"LocationURL: {urlError}";

                    return url is string text && text.Length > 0
                        ? $"LocationURL = {text}"
                        : "LocationURL is empty";
                }
                finally
                {
                    Release(window);
                }
            }

            return typed is null
                ? "window not found (late-bound)"
                : "window not found";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
        finally
        {
            Release(raw);
        }
    }

    private static string Describe(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
            return $"{inner.GetType().Name} 0x{inner.HResult:X8} {inner.Message}";

        return $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
    }

    /// <summary>Reads a dispatch property, reporting why it failed when it did.</summary>
    private static object? Read(object? instance, string property, out string? error, params object?[] args)
    {
        error = null;
        if (instance is null)
            return null;

        try
        {
            var flags = args.Length == 0 ? BindingFlags.GetProperty : BindingFlags.InvokeMethod;
            return instance.GetType().InvokeMember(property, flags, null, instance, args.Length == 0 ? null : args);
        }
        catch (Exception ex)
        {
            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            error = $"0x{actual.HResult:X8} {actual.Message}";
            return null;
        }
    }

    private static object? Read(object? instance, string property) => Read(instance, property, out _);

    private static string ReadLocation(object window)
    {
        if (Read(window, "LocationURL") is string url && !string.IsNullOrWhiteSpace(url))
            return PathNormalizer.Normalize(url);

        // Virtual folders only expose their PIDL backed path.
        try
        {
            var document = Read(window, "Document");
            var folder = document is null ? null : Read(document, "Folder");
            var self = folder is null ? null : Read(folder, "Self");
            if (self is not null && Read(self, "Path") is string path && !string.IsNullOrWhiteSpace(path))
                return PathNormalizer.Normalize(path);
        }
        catch
        {
            // Not a ShellFolderView.
        }

        return string.Empty;
    }

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static long ToLong(object? value)
    {
        try
        {
            return value switch
            {
                null => 0,
                int i => i,
                long l => l,
                nint n => (long)n,
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static nint ParentWindowOf(nint hwnd)
    {
        if (NativeMethods.HasClassName(hwnd, NativeMethods.ExplorerWindowClass))
            return hwnd;

        var parent = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_PARENT);
        return NativeMethods.HasClassName(parent, NativeMethods.ExplorerWindowClass) ? parent : 0;
    }

    private static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
            return;

        try { Marshal.ReleaseComObject(instance); } catch { /* ignore */ }
    }
}
