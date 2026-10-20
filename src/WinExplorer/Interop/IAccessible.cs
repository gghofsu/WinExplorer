using System.Runtime.InteropServices;

namespace WinExplorer.Interop;

/// <summary>
/// Minimal MSAA (Active Accessibility) interface. The member order is significant:
/// COM vtable layout must match oleacc's <c>IAccessible</c> exactly, so every method up
/// to the last one we call is declared, even the ones we never use.
/// </summary>
[ComImport]
[TypeLibType(TypeLibTypeFlags.FHidden | TypeLibTypeFlags.FDual | TypeLibTypeFlags.FDispatchable)]
[Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
internal interface IAccessible
{
    [DispId(-5000)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? get_accParent();

    [DispId(-5001)]
    [TypeLibFunc(64)]
    int get_accChildCount();

    [TypeLibFunc(64)]
    [DispId(-5002)]
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? get_accChild([In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5003)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accName([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5004)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accValue([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5005)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accDescription([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5006)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_accRole([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5007)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_accState([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5008)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accHelp([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5009)]
    [TypeLibFunc(64)]
    int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string? helpFile,
        [Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5010)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accKeyboardShortcut([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5011)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_accFocus();

    [DispId(-5012)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_accSelection();

    [TypeLibFunc(64)]
    [DispId(-5013)]
    [return: MarshalAs(UnmanagedType.BStr)]
    string? get_accDefaultAction([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [TypeLibFunc(64)]
    [DispId(-5014)]
    void accSelect(int flagsSelect, [Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5015)]
    [TypeLibFunc(64)]
    void accLocation(out int left, out int top, out int width, out int height,
        [Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5016)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? accNavigate(int navDir, [Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varStart);

    [DispId(-5017)]
    [TypeLibFunc(64)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? accHitTest(int xLeft, int yTop);

    [DispId(-5018)]
    [TypeLibFunc(64)]
    void accDoDefaultAction([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild);

    [DispId(-5003)]
    [TypeLibFunc(64)]
    void set_accName([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild,
        [MarshalAs(UnmanagedType.BStr)] string? pszName);

    [DispId(-5004)]
    [TypeLibFunc(64)]
    void set_accValue([Optional] [In] [MarshalAs(UnmanagedType.Struct)] object varChild,
        [MarshalAs(UnmanagedType.BStr)] string? pszValue);
}

/// <summary>Minimal <c>IOleWindow</c>: lets us turn an accessible object into an HWND.</summary>
[ComImport]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    [PreserveSig]
    int GetWindow(out nint hwnd);

    [PreserveSig]
    int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}

/// <summary>MSAA role numbers (subset of oleacc.h we care about).</summary>
internal static class MsaaRole
{
    internal const int Window = 0x09;
    internal const int Client = 0x0A;
    internal const int Document = 0x0F;
    internal const int Pane = 0x10;
    internal const int List = 0x21;
    internal const int ListItem = 0x22;
    internal const int Outline = 0x23;
    internal const int OutlineItem = 0x24;

    internal static bool IsItem(int role) => role is ListItem or OutlineItem;

    internal static string Describe(int role) => role switch
    {
        Window => "window",
        Client => "client",
        Document => "document",
        Pane => "pane",
        List => "list",
        ListItem => "list item",
        Outline => "tree",
        OutlineItem => "tree item",
        _ => $"role 0x{role:X}"
    };
}
