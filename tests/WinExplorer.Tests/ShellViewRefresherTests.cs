using WinExplorer.Interop;
using WinExplorer.Shell;
using Xunit;

namespace WinExplorer.Tests;

public class ShellViewRefresherTests
{
    [Fact]
    public void Refresh_targets_the_tabs_actual_shell_view()
    {
        var provider = new FakeProvider();

        Assert.True(ShellViewRefresher.TryRefresh(provider, 20));
        Assert.Equal(1, provider.Browser.View.RefreshCount);
        Assert.Equal(ShellViewRefresher.TopLevelBrowserService, provider.RequestedService);
        Assert.Equal(typeof(IShellBrowser).GUID, provider.RequestedInterface);
    }

    [Fact]
    public void Another_tabs_view_is_never_refreshed()
    {
        var provider = new FakeProvider();

        Assert.False(ShellViewRefresher.TryRefresh(provider, 99));
        Assert.Equal(0, provider.Browser.View.RefreshCount);
    }

    [Fact]
    public void Missing_service_allows_the_automation_fallback()
    {
        var provider = new FakeProvider { Result = -1 };

        Assert.False(ShellViewRefresher.TryRefresh(provider, 20));
        Assert.Equal(0, provider.Browser.View.RefreshCount);
    }

    [Fact]
    public void Missing_view_allows_the_automation_fallback()
    {
        var provider = new FakeProvider();
        provider.Browser.ViewResult = -1;

        Assert.False(ShellViewRefresher.TryRefresh(provider, 20));
        Assert.Equal(0, provider.Browser.View.RefreshCount);
    }

    [Fact]
    public void A_failed_refresh_is_not_reported_as_success()
    {
        var provider = new FakeProvider();
        provider.Browser.View.Result = -1;

        Assert.False(ShellViewRefresher.TryRefresh(provider, 20));
        Assert.Equal(1, provider.Browser.View.RefreshCount);
    }

    [Fact]
    public void A_closing_view_does_not_throw_out_of_the_refresh_path()
    {
        var provider = new FakeProvider();
        provider.Browser.View.Throw = true;

        Assert.False(ShellViewRefresher.TryRefresh(provider, 20));
    }

    private sealed class FakeProvider : IServiceProviderNative
    {
        public FakeBrowser Browser { get; } = new();
        public int Result { get; init; }
        public Guid RequestedService { get; private set; }
        public Guid RequestedInterface { get; private set; }

        public int QueryService(ref Guid guidService, ref Guid riid, out IShellBrowser? service)
        {
            RequestedService = guidService;
            RequestedInterface = riid;
            service = Result >= 0 ? Browser : null;
            return Result;
        }
    }

    private sealed class FakeBrowser : IShellBrowser
    {
        public FakeView View { get; } = new();
        public int ViewResult { get; set; }

        public int GetWindow(out nint hwnd) { hwnd = 20; return 0; }
        public void _VtblGap1_11() { }
        public int QueryActiveShellView(out IShellView? view)
        {
            view = ViewResult >= 0 ? View : null;
            return ViewResult;
        }
    }

    private sealed class FakeView : IShellView
    {
        public int RefreshCount { get; private set; }
        public int Result { get; set; }
        public bool Throw { get; set; }

        public int GetWindow(out nint hwnd) { hwnd = 30; return 0; }
        public void _VtblGap1_4() { }
        public int Refresh()
        {
            RefreshCount++;
            if (Throw)
                throw new InvalidOperationException("The tab closed.");
            return Result;
        }
    }
}
