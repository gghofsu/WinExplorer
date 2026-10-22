using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinExplorer.Interop;

/// <summary>
/// Hand-written interop for the parts of <c>SHDocVw</c> that w4po/ExplorerTabUtility gets
/// from the COM type library (<c>tlbimp</c> is Windows-only, so the interfaces are declared
/// here instead).
/// </summary>
/// <remarks>
/// Every File Explorer tab is published through <c>ShellWindows</c> as an object that
/// implements <see cref="IWebBrowser2"/>. The member order below is the exact vtable order
/// of the dual interface chain <c>IWebBrowser</c> → <c>IWebBrowserApp</c> →
/// <c>IWebBrowser2</c> from <c>exdisp.idl</c>; do not reorder or remove members.
/// </remarks>
[ComImport]
[Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWebBrowser2
{
    // ---- IWebBrowser ----
    void GoBack();
    void GoForward();
    void GoHome();
    void GoSearch();

    void Navigate([MarshalAs(UnmanagedType.BStr)] string url,
        [In] ref object flags, [In] ref object targetFrameName, [In] ref object postData, [In] ref object headers);

    void Refresh();
    void Refresh2([In] ref object level);
    void Stop();

    object Application { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Parent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Container { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    object Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
    bool TopLevelContainer { get; }
    string Type { [return: MarshalAs(UnmanagedType.BStr)] get; }

    int Left { get; set; }
    int Top { get; set; }
    int Width { get; set; }
    int Height { get; set; }

    string LocationName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string LocationURL { [return: MarshalAs(UnmanagedType.BStr)] get; }
    bool Busy { get; }

    // ---- IWebBrowserApp ----
    void Quit();
    void ClientToWindow(ref int pcx, ref int pcy);
    void PutProperty([MarshalAs(UnmanagedType.BStr)] string property, object value);
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string property);

    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }

    /// <summary>The top level frame window (<c>CabinetWClass</c>) this tab belongs to.</summary>
    int HWND { get; }

    string FullName { [return: MarshalAs(UnmanagedType.BStr)] get; }
    string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }

    bool Visible { get; set; }
    bool StatusBar { get; set; }
    string StatusText { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    int ToolBar { get; set; }
    bool MenuBar { get; set; }
    bool FullScreen { get; set; }

    // ---- IWebBrowser2 ----
    void Navigate2([In] ref object url,
        [In] ref object flags, [In] ref object targetFrameName, [In] ref object postData, [In] ref object headers);

    int QueryStatusWB(int cmdId);
    void ExecWB(int cmdId, int cmdExecOpt, [In] ref object pvaIn, [In, Out] ref object pvaOut);
    void ShowBrowserBar([In] ref object pvaClsid, [In] ref object pvarShow, [In] ref object pvarSize);

    int ReadyState { get; }
    bool Offline { get; set; }
    bool Silent { get; set; }
    bool RegisterAsBrowser { get; set; }
    bool RegisterAsDropTarget { get; set; }
    bool TheaterMode { get; set; }
    bool AddressBar { get; set; }
    bool Resizable { get; set; }
}

/// <summary>
/// The events of the <c>ShellWindows</c> collection. Only the two members WinExplorer
/// subscribes to are declared; the dispatch ids are what matters.
/// </summary>
[ComVisible(true)]
[Guid("FE4106E0-399A-11D0-A48C-00A0C90A8F39")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DShellWindowsEvents
{
    [DispId(200)]
    void WindowRegistered(int cookie);

    [DispId(201)]
    void WindowRevoked(int cookie);
}

/// <summary>
/// The events of one shell window (tab). Only the members WinExplorer subscribes to are
/// declared; Explorer raises many more, which the sink simply does not implement.
/// </summary>
[ComVisible(true)]
[Guid("34A715A0-6587-11D0-924A-0020AFC7AC4D")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DWebBrowserEvents2
{
    [DispId(252)]
    void NavigateComplete2([MarshalAs(UnmanagedType.IDispatch)] object pDisp, ref object url);

    [DispId(253)]
    void OnQuit();
}

/// <summary>Forwards <see cref="DShellWindowsEvents"/> to delegates.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ShellWindowsEventsSink : DShellWindowsEvents
{
    private readonly Action<int> _registered;
    private readonly Action<int> _revoked;

    public ShellWindowsEventsSink(Action<int> registered, Action<int> revoked)
    {
        _registered = registered;
        _revoked = revoked;
    }

    public void WindowRegistered(int cookie) => _registered(cookie);

    public void WindowRevoked(int cookie) => _revoked(cookie);
}

/// <summary>Forwards <see cref="DWebBrowserEvents2"/> to delegates.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WebBrowserEventsSink : DWebBrowserEvents2
{
    private readonly Action? _navigateComplete;
    private readonly Action? _quit;

    public WebBrowserEventsSink(Action? navigateComplete, Action? quit)
    {
        _navigateComplete = navigateComplete;
        _quit = quit;
    }

    public void NavigateComplete2(object pDisp, ref object url) => _navigateComplete?.Invoke();

    public void OnQuit() => _quit?.Invoke();
}

/// <summary>
/// One advised COM connection point (an event subscription). Create and dispose on the same
/// STA thread that owns the event source.
/// </summary>
internal sealed class ComEventSubscription : IDisposable
{
    private IConnectionPoint? _connectionPoint;
    private int _cookie;

    private ComEventSubscription(IConnectionPoint connectionPoint, int cookie)
    {
        _connectionPoint = connectionPoint;
        _cookie = cookie;
    }

    /// <summary>Advises <paramref name="sink"/> on <paramref name="source"/>, or null.</summary>
    public static ComEventSubscription? TryAdvise(object source, Guid eventInterfaceId, object sink)
    {
        try
        {
            if (source is not IConnectionPointContainer container)
                return null;

            container.FindConnectionPoint(ref eventInterfaceId, out var connectionPoint);
            if (connectionPoint is null)
                return null;

            connectionPoint.Advise(sink, out var cookie);
            return new ComEventSubscription(connectionPoint, cookie);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        var connectionPoint = _connectionPoint;
        _connectionPoint = null;
        if (connectionPoint is null)
            return;

        try
        {
            if (_cookie != 0)
                connectionPoint.Unadvise(_cookie);
        }
        catch
        {
            // The event source (Explorer) may already be gone.
        }
        finally
        {
            _cookie = 0;
            try { Marshal.ReleaseComObject(connectionPoint); }
            catch { /* ignore */ }
        }
    }
}
