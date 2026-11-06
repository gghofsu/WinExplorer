using WinExplorer.Shell;
using Xunit;

namespace WinExplorer.Tests;

/// <summary>
/// Regression tests for the duplicate-check desktop rules. Deciding "same desktop" is what
/// keeps a folder that is open on another virtual desktop from being merged into, and what
/// lets the same-window dedupe work without a single COM call (one frame is one desktop).
/// </summary>
public class DesktopMatchTests
{
    private static readonly Guid Desktop1 = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Desktop2 = new("20000000-0000-0000-0000-000000000002");

    [Fact]
    public void A_tab_in_the_requesting_window_is_always_in_scope()
    {
        // No COM probe is needed for same-frame candidates, and none is trusted either.
        Assert.True(DesktopMatch.IsInScope(new(Desktop1), frame: 42, sameWindowFrame: 42,
            liveDesktopId: null, cachedDesktopId: null, liveOnCurrentDesktop: false));

        // Even for current-desktop scopes: the request is by definition on the current desktop.
        Assert.True(DesktopMatch.IsInScope(new(Desktop1, CurrentDesktopOnly: true), frame: 42, sameWindowFrame: 42,
            liveDesktopId: null, cachedDesktopId: null, liveOnCurrentDesktop: false));
    }

    [Fact]
    public void Another_window_on_the_same_desktop_is_in_scope()
    {
        Assert.True(DesktopMatch.IsInScope(new(Desktop1), frame: 7, sameWindowFrame: 42,
            liveDesktopId: Desktop1, cachedDesktopId: null, liveOnCurrentDesktop: true));
    }

    [Fact]
    public void A_window_tracked_earlier_wins_when_the_live_query_fails()
    {
        // A wedged or flaky shell must not hide a tab that plainly is open: the id stored
        // when the tab registered fills in for a failed live query.
        Assert.True(DesktopMatch.IsInScope(new(Desktop1), frame: 7, sameWindowFrame: 0,
            liveDesktopId: null, cachedDesktopId: Desktop1, liveOnCurrentDesktop: false));

        // And it must never leak the match to another desktop.
        Assert.False(DesktopMatch.IsInScope(new(Desktop1), frame: 7, sameWindowFrame: 0,
            liveDesktopId: null, cachedDesktopId: Desktop2, liveOnCurrentDesktop: false));
    }

    [Fact]
    public void A_successful_live_query_beats_a_stale_cache()
    {
        // The window was moved in Task View after it registered: the live answer wins.
        Assert.False(DesktopMatch.IsInScope(new(Desktop1), frame: 7, sameWindowFrame: 0,
            liveDesktopId: Desktop2, cachedDesktopId: Desktop1, liveOnCurrentDesktop: true));
    }

    [Fact]
    public void An_unverifiable_candidate_is_rejected_not_trusted()
    {
        Assert.False(DesktopMatch.IsInScope(new(Desktop1), frame: 7, sameWindowFrame: 0,
            liveDesktopId: null, cachedDesktopId: null, liveOnCurrentDesktop: true));
    }

    [Fact]
    public void An_unknown_scope_never_matches_even_an_unknown_window()
    {
        Assert.False(DesktopMatch.IsInScope(new(null), frame: 7, sameWindowFrame: 0,
            liveDesktopId: null, cachedDesktopId: null, liveOnCurrentDesktop: true));
        Assert.False(DesktopMatch.IsInScope(new(Guid.Empty), frame: 7, sameWindowFrame: 0,
            liveDesktopId: Guid.Empty, cachedDesktopId: null, liveOnCurrentDesktop: true));
    }

    [Fact]
    public void A_current_desktop_scope_requires_the_live_current_desktop_probe()
    {
        var scope = new ExplorerDesktopScope(Desktop1, CurrentDesktopOnly: true);
        Assert.False(DesktopMatch.IsInScope(scope, frame: 7, sameWindowFrame: 0,
            liveDesktopId: Desktop1, cachedDesktopId: null, liveOnCurrentDesktop: false));
        Assert.True(DesktopMatch.IsInScope(scope, frame: 7, sameWindowFrame: 0,
            liveDesktopId: Desktop1, cachedDesktopId: null, liveOnCurrentDesktop: true));
    }
}
