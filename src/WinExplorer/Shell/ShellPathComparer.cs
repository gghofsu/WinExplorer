using System.Runtime.InteropServices;
using WinExplorer.Core;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>
/// Compares folders the way Windows does: by shell item id (PIDL) instead of string
/// comparison. This makes <c>C:\PROGRA~1</c>, <c>C:\Program Files</c> and a junction all
/// compare equal, and works for virtual folders too.
/// </summary>
/// <remarks>Must be created and used on an STA thread.</remarks>
internal sealed class ShellPathComparer : IDisposable
{
    private IShellFolder? _desktopFolder;
    private bool _disposed;

    public ShellPathComparer()
    {
        if (NativeMethods.SHGetDesktopFolder(out var desktop) != 0 || desktop == 0)
            return;

        try
        {
            _desktopFolder = (IShellFolder)Marshal.GetObjectForIUnknown(desktop);
        }
        finally
        {
            Marshal.Release(desktop);
        }
    }

    public bool IsUsable => _desktopFolder is not null;

    /// <summary>Parses a path into a PIDL. The caller owns the returned memory.</summary>
    public nint ParseToPidl(string path)
    {
        if (_desktopFolder is null || string.IsNullOrWhiteSpace(path))
            return 0;

        try
        {
            var attributes = 0u;
            return _desktopFolder.ParseDisplayName(0, 0, path, out _, out var pidl, ref attributes) == 0 ? pidl : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>True when both paths resolve to the same shell item.</summary>
    public bool IsEquivalent(string? path1, string? path2)
    {
        var left = PathNormalizer.Normalize(path1);
        var right = PathNormalizer.Normalize(path2);

        if (left.Length == 0 || right.Length == 0)
            return false;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        if (_desktopFolder is null)
            return false;

        nint pidl1 = 0, pidl2 = 0;
        try
        {
            pidl1 = ParseToPidl(left);
            pidl2 = ParseToPidl(right);
            return pidl1 != 0 && pidl2 != 0 && ComparePidls(pidl1, pidl2);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl1 != 0) Marshal.FreeCoTaskMem(pidl1);
            if (pidl2 != 0) Marshal.FreeCoTaskMem(pidl2);
        }
    }

    private bool ComparePidls(nint pidl1, nint pidl2)
    {
        if (_desktopFolder is null)
            return false;

        try
        {
            if (_desktopFolder.CompareIDs(0, pidl1, pidl2) == 0)
                return true;

            // Canonical comparison ignores display name differences.
            if (_desktopFolder.CompareIDs((nint)NativeMethods.SHCIDS_CANONICALONLY, pidl1, pidl2) == 0)
                return true;
        }
        catch
        {
            // fall through to the string based comparison below
        }

        var name1 = NativeMethods.GetNameFromPidl(pidl1);
        var name2 = NativeMethods.GetNameFromPidl(pidl2);
        return !string.IsNullOrEmpty(name1) && string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_desktopFolder is not null)
        {
            try { Marshal.ReleaseComObject(_desktopFolder); }
            catch { /* ignore */ }
            _desktopFolder = null;
        }
    }
}
