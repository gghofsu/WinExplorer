using WinExplorer.Shell;
using Xunit;

namespace WinExplorer.Tests;

public class ExplorerWindowRouterTests
{
    private static readonly Guid Desktop1 = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Desktop2 = new("20000000-0000-0000-0000-000000000002");

    [Fact]
    public void First_folder_on_an_empty_desktop_needs_a_new_window()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(1, Desktop1, 8) };

        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop1), windows));
        Assert.Equal((nint)0, router.GetMainWindow(new(Desktop2), windows));
    }

    [Fact]
    public void Each_desktop_remembers_its_own_main_window()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(2, Desktop2), Window(1, Desktop1, 8) };

        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop1), windows));
        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), windows));
        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop1), windows));
        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), windows));
    }

    [Fact]
    public void New_window_only_merges_into_another_window_on_its_own_desktop()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(3, Desktop2), Window(2, Desktop2, 2), Window(1, Desktop1, 8) };

        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), windows, excludeFrame: 3));
        Assert.Equal((nint)0, router.GetMainWindow(new(Desktop1), windows, excludeFrame: 1));
    }

    [Fact]
    public void A_preferred_window_on_another_desktop_is_ignored()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(2, Desktop2), Window(1, Desktop1) };

        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), windows, preferred: 1));
    }

    [Fact]
    public void Preferred_window_is_honoured_only_on_the_matching_desktop()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(3, Desktop2), Window(2, Desktop2, 4), Window(1, Desktop1, 8) };

        Assert.Equal((nint)3, router.GetMainWindow(new(Desktop2), windows, preferred: 3));
        Assert.Equal((nint)3, router.GetMainWindow(new(Desktop2), windows));
        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop1), windows));
    }

    [Fact]
    public void Closing_a_main_window_selects_another_on_the_same_desktop()
    {
        var router = new ExplorerWindowRouter();
        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), new[] { Window(2, Desktop2, 3) }));

        var windows = new[] { Window(3, Desktop2), Window(1, Desktop1, 8) };
        Assert.Equal((nint)3, router.GetMainWindow(new(Desktop2), windows));
    }

    [Fact]
    public void Moving_the_main_window_in_task_view_invalidates_the_old_desktop_cache()
    {
        var router = new ExplorerWindowRouter();
        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop1), new[] { Window(1, Desktop1, 8) }));

        var windows = new[] { Window(2, Desktop1), Window(1, Desktop2, 8) };
        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop1), windows));
        Assert.Equal((nint)1, router.GetMainWindow(new(Desktop2), windows));
    }

    [Fact]
    public void Unknown_desktops_never_match_even_each_other()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[] { Window(1, null), Window(2, Guid.Empty), Window(3, Desktop1) };

        Assert.Equal((nint)0, router.GetMainWindow(new(null), windows, preferred: 1));
        Assert.Equal((nint)0, router.GetMainWindow(new(Guid.Empty), windows, preferred: 2));
        Assert.Equal((nint)0, router.GetMainWindow(new(Desktop2), windows));
        Assert.False(new ExplorerDesktopScope(null).Contains(null));
        Assert.False(new ExplorerDesktopScope(Guid.Empty).Contains(Guid.Empty));
    }

    [Fact]
    public void Most_tabs_wins_and_ties_prefer_the_backmost_window_on_that_desktop()
    {
        var router = new ExplorerWindowRouter();
        var windows = new[]
        {
            Window(4, Desktop2, 2), Window(3, Desktop2, 3), Window(2, Desktop2, 3), Window(1, Desktop1, 9)
        };

        Assert.Equal((nint)2, router.GetMainWindow(new(Desktop2), windows));
    }

    [Fact]
    public void Duplicate_search_scope_only_includes_the_requested_desktop()
    {
        var scope = new ExplorerDesktopScope(Desktop2);
        Assert.True(scope.Contains(Desktop2));
        Assert.False(scope.Contains(Desktop1));
        Assert.False(scope.Contains(null));
        Assert.False(scope.Contains(Guid.Empty));
    }

    [Fact]
    public void Direct_requests_do_not_use_a_window_after_the_user_switches_desktops()
    {
        var router = new ExplorerWindowRouter();
        var scope = new ExplorerDesktopScope(Desktop1, CurrentDesktopOnly: true);
        var windows = new[] { new ExplorerWindowInfo(1, Desktop1, 8, IsOnCurrentDesktop: false) };

        Assert.Equal((nint)0, router.GetMainWindow(scope, windows, preferred: 1));
        Assert.False(scope.Contains(Desktop1, isOnCurrentDesktop: false));
        // A delayed registration remains anchored to its source desktop, not the tray's.
        Assert.True(new ExplorerDesktopScope(Desktop1).Contains(Desktop1, isOnCurrentDesktop: false));
    }

    private static ExplorerWindowInfo Window(int handle, Guid? desktop, int tabs = 1) =>
        new((nint)handle, desktop, tabs);
}
