using System.Runtime.InteropServices;

namespace WinExplorer.Interop;

/// <summary>
/// The COM class behind <c>Shell.Application</c>'s <c>Windows</c> collection
/// (<c>CLSID_ShellWindows</c>). Creating it directly avoids the late-bound
/// <c>Shell.Application</c> object that can come back empty.
/// </summary>
[ComImport]
[Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
internal class ShellWindowsClass
{
}

/// <summary>
/// <c>IShellWindows</c> (the interface behind <c>SHDocVw.ShellWindows</c>).
/// </summary>
/// <remarks>
/// Only <see cref="get_Count"/> and <see cref="Item"/> are used, but the four IDispatch
/// slots in front of them are declared because the vtable layout is what matters: the
/// interface is a dual interface, so the methods sit at fixed slots after IDispatch.
/// </remarks>
[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellWindows
{
    // ---- IDispatch ----

    [PreserveSig]
    int GetTypeInfoCount(out uint count);

    [PreserveSig]
    int GetTypeInfo(uint typeInfo, uint lcid, out nint info);

    [PreserveSig]
    int GetIDsOfNames(ref Guid riid,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        uint nameCount, uint lcid, [MarshalAs(UnmanagedType.LPArray)] int[] ids);

    [PreserveSig]
    int Invoke(int dispId, ref Guid riid, uint lcid, ushort flags, nint parameters,
        nint result, nint exception, nint argumentError);

    // ---- IShellWindows ----

    [PreserveSig]
    int get_Count(out int count);

    [PreserveSig]
    int Item([MarshalAs(UnmanagedType.Struct)] object index,
        [MarshalAs(UnmanagedType.IDispatch)] out object? window);

    [PreserveSig]
    int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? enumerator);

    [PreserveSig]
    int Register([MarshalAs(UnmanagedType.IDispatch)] object window, int windowId, int kind,
        [MarshalAs(UnmanagedType.Struct)] object cookie, out int result);

    [PreserveSig]
    int RegisterPending(int threadId, nint location, int flags, int reserved,
        [MarshalAs(UnmanagedType.Struct)] object cookie, out int windowId);

    [PreserveSig]
    int Revoke([MarshalAs(UnmanagedType.Struct)] object cookie);

    [PreserveSig]
    int OnNavigate([MarshalAs(UnmanagedType.Struct)] object cookie, nint location);

    [PreserveSig]
    int OnActivated([MarshalAs(UnmanagedType.Struct)] object cookie, int captured, int isActive);

    [PreserveSig]
    int FindWindowSW([MarshalAs(UnmanagedType.Struct)] object location, [MarshalAs(UnmanagedType.Struct)] object root,
        int kind, out int hwnd, int flags);

    [PreserveSig]
    int OnCreated([MarshalAs(UnmanagedType.Struct)] object cookie, [MarshalAs(UnmanagedType.IDispatch)] object window);

    [PreserveSig]
    int ProcessAttachDetach(int attach);
}
