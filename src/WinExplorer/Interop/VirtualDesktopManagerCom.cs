using System.Runtime.InteropServices;

namespace WinExplorer.Interop;

// The documented virtual desktop API, available since Windows 10. No private,
// Windows-build-specific interfaces or moving windows between desktops are needed.
[ComImport]
[Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
internal class VirtualDesktopManagerClass
{
}

[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(nint window, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(nint window, [In] ref Guid desktopId);
}
