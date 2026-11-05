using WinExplorer.Shell;
using Xunit;

namespace WinExplorer.Tests;

public class TabSwitcherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reused_tabs_are_refreshed_after_selection_and_optional_activation(bool activate)
    {
        var calls = new List<string>();
        var selected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var switching = TabSwitcher.SwitchAsync(10, 20, activate,
            frame =>
            {
                Assert.Equal((nint)10, frame);
                calls.Add("activate");
                return Task.CompletedTask;
            },
            (frame, tab) =>
            {
                Assert.Equal((nint)10, frame);
                Assert.Equal((nint)20, tab);
                calls.Add("select");
                selecting.SetResult(true);
                return selected.Task;
            },
            tab =>
            {
                Assert.Equal((nint)20, tab);
                calls.Add("refresh");
                return Task.CompletedTask;
            });

        await selecting.Task;
        // Refresh must wait for Explorer to confirm the destination, not just for the
        // select command to have been posted. --no-activate must still refresh.
        Assert.DoesNotContain("refresh", calls);
        Assert.False(switching.IsCompleted);
        selected.SetResult(true);

        Assert.True(await switching);
        Assert.Equal(activate ? new[] { "activate", "select", "refresh" } : new[] { "select", "refresh" }, calls);
    }

    [Fact]
    public async Task Failed_selection_does_not_refresh_the_wrong_view_or_report_a_switch()
    {
        var refreshed = false;
        var result = await TabSwitcher.SwitchAsync(10, 20, true,
            _ => Task.CompletedTask,
            (_, _) => Task.FromResult(false),
            _ =>
            {
                refreshed = true;
                return Task.CompletedTask;
            });

        Assert.False(result);
        Assert.False(refreshed);
    }

    [Fact]
    public async Task Opening_the_already_selected_tab_still_refreshes_it()
    {
        var refreshes = 0;
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await TabSwitcher.SwitchAsync(10, 20, true,
                _ => Task.CompletedTask,
                (_, _) => Task.FromResult(true),
                _ =>
                {
                    refreshes++;
                    return Task.CompletedTask;
                }));
        }

        Assert.Equal(2, refreshes);
    }
}
