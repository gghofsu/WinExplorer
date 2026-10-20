using System.Runtime.InteropServices;

namespace WinExplorer.Interop;

/// <summary>
/// The first four methods of <c>IShellFolder</c>; enough to parse a display name into a
/// PIDL and to ask the shell whether two PIDLs are the same folder.
/// </summary>
[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    /// <summary>Translates a folder path into a PIDL (allocated with the COM task allocator).</summary>
    [PreserveSig]
    int ParseDisplayName(nint hwnd, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        out uint eaten, out nint pidl, ref uint attributes);

    // Skip EnumObjects, BindToObject and BindToStorage. This magic name makes the CLR
    // leave a gap in the vtable (three entries).
    void _VtblGap1_3();

    /// <summary>Returns 0 when both PIDLs identify the same object.</summary>
    [PreserveSig]
    int CompareIDs(nint lParam, nint pidl1, nint pidl2);
}
