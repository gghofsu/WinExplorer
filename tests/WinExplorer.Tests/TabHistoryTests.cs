using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class TabHistoryTests
{
    [Fact]
    public void Most_recent_folder_is_returned_first()
    {
        var history = new TabHistory();
        history.RecordClosed(@"C:\one");
        history.RecordClosed(@"C:\two");

        Assert.Equal(@"C:\two", history.TakeMostRecent()!.Path);
        Assert.Equal(@"C:\one", history.TakeMostRecent()!.Path);
        Assert.Null(history.TakeMostRecent());
    }

    [Fact]
    public void Recording_the_same_folder_twice_moves_it_to_the_front()
    {
        var history = new TabHistory();
        history.RecordClosed(@"C:\one");
        history.RecordClosed(@"C:\two");
        history.RecordClosed(@"c:\ONE");

        Assert.Equal(2, history.Count);
        Assert.Equal(@"C:\one", history.TakeMostRecent()!.Path, ignoreCase: true);
    }

    [Fact]
    public void Capacity_is_respected()
    {
        var history = new TabHistory(capacity: 3);
        for (var i = 0; i < 10; i++)
            history.RecordClosed($@"C:\folder{i}");

        Assert.Equal(3, history.Count);
        Assert.Equal(@"C:\folder9", history.TakeMostRecent()!.Path);
    }

    [Fact]
    public void Empty_locations_are_ignored()
    {
        var history = new TabHistory();
        history.RecordClosed(null);
        history.RecordClosed("   ");
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void Forget_removes_an_entry()
    {
        var history = new TabHistory();
        history.RecordClosed(@"C:\keep");
        history.RecordClosed(@"C:\drop");
        history.Forget(@"C:\DROP");

        Assert.Single(history.Snapshot());
        Assert.Equal(@"C:\keep", history.TakeMostRecent()!.Path);
    }

    [Fact]
    public void WasClosedRecently_matches_fresh_entries_only()
    {
        var history = new TabHistory();
        history.RecordClosed(@"C:\old", DateTimeOffset.Now.AddSeconds(-30));
        history.RecordClosed(@"C:\fresh");

        Assert.True(history.WasClosedRecently(@"c:\FRESH", maxAgeMs: 2000));
        Assert.False(history.WasClosedRecently(@"C:\old", maxAgeMs: 2000));
        Assert.False(history.WasClosedRecently(@"C:\never", maxAgeMs: 2000));
        Assert.False(history.WasClosedRecently(null, maxAgeMs: 2000));
    }

    [Fact]
    public void The_same_folder_can_be_closed_independently_on_two_desktops()
    {
        var history = new TabHistory();
        var desktop1 = Guid.NewGuid();
        var desktop2 = Guid.NewGuid();
        history.RecordClosed(@"C:\shared", desktopId: desktop1);
        history.RecordClosed(@"C:\shared", desktopId: desktop2);

        Assert.Equal(2, history.Count);
        Assert.Equal(desktop1, history.TakeMostRecent(desktop1)!.DesktopId);
        Assert.Equal(desktop2, history.TakeMostRecent(desktop2)!.DesktopId);
    }

    [Fact]
    public void Reopen_only_takes_a_folder_from_the_requested_desktop()
    {
        var history = new TabHistory();
        var desktop1 = Guid.NewGuid();
        var desktop2 = Guid.NewGuid();
        history.RecordClosed(@"C:\desktop1", desktopId: desktop1);
        history.RecordClosed(@"C:\desktop2", desktopId: desktop2);

        Assert.Equal(@"C:\desktop1", history.TakeMostRecent(desktop1)!.Path);
        Assert.Null(history.TakeMostRecent(desktop1));
        Assert.Equal(@"C:\desktop2", history.TakeMostRecent(desktop2)!.Path);
    }

    [Fact]
    public void A_close_on_another_desktop_is_not_mistaken_for_a_dragged_out_tab()
    {
        var history = new TabHistory();
        var desktop1 = Guid.NewGuid();
        var desktop2 = Guid.NewGuid();
        history.RecordClosed(@"C:\shared", desktopId: desktop1);

        Assert.True(history.WasClosedRecently(@"C:\shared", 2000, desktop1));
        Assert.False(history.WasClosedRecently(@"C:\shared", 2000, desktop2));
    }

    [Fact]
    public void Reopening_a_folder_does_not_clear_another_desktops_history()
    {
        var history = new TabHistory();
        var desktop1 = Guid.NewGuid();
        var desktop2 = Guid.NewGuid();
        history.RecordClosed(@"C:\shared", desktopId: desktop1);
        history.RecordClosed(@"C:\shared", desktopId: desktop2);

        history.Forget(@"C:\shared", desktop1);

        Assert.Single(history.Snapshot());
        Assert.Equal(desktop2, history.TakeMostRecent()!.DesktopId);
    }

    [Fact]
    public void Snapshot_is_ordered_newest_first()
    {
        var history = new TabHistory();
        history.RecordClosed(@"C:\a");
        history.RecordClosed(@"C:\b");

        var snapshot = history.Snapshot();
        Assert.Equal(@"C:\b", snapshot[0].Path);
        Assert.Equal(@"C:\a", snapshot[1].Path);
    }
}
