using System.Runtime.InteropServices;
using WinExplorer.Interop;

namespace WinExplorer.Shell;

/// <summary>Refreshes the folder view itself, rather than relying only on browser automation.</summary>
internal static class ShellViewRefresher
{
    internal static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary>Runs on the shell STA, after the destination tab has been selected.</summary>
    public static bool TryRefresh(IServiceProviderNative provider, nint tab)
    {
        IShellBrowser? browser = null;
        IShellView? view = null;
        try
        {
            var service = TopLevelBrowserService;
            var iid = typeof(IShellBrowser).GUID;
            if (provider.QueryService(ref service, ref iid, out browser) < 0 || browser is null)
                return false;

            // Do not refresh a different tab if a navigation/reparent changed the object.
            if (browser.GetWindow(out var handle) < 0 || handle != tab || tab == 0)
                return false;

            return browser.QueryActiveShellView(out view) >= 0 && view is not null && view.Refresh() >= 0;
        }
        catch
        {
            // The caller can fall back to IWebBrowser2.Refresh on older shell versions.
            return false;
        }
        finally
        {
            Release(view);
            Release(browser);
        }
    }

    private static void Release(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch { /* A closing Explorer must not prevent the refresh fallback. */ }
    }
}
