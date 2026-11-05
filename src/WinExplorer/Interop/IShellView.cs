using System.Runtime.InteropServices;

namespace WinExplorer.Interop;

/// <summary>The beginning of IShellView (including its IOleWindow base).</summary>
[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig]
    int GetWindow(out nint hwnd);

    // ContextSensitiveHelp, TranslateAccelerator, EnableModeless, UIActivate.
    void _VtblGap1_4();

    /// <summary>Re-enumerates the folder view, just like Explorer's Refresh/F5.</summary>
    [PreserveSig]
    int Refresh();
}
