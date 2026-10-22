using System.IO;
using WinExplorer.Core;
using WinExplorer.Services;
using Xunit;

namespace WinExplorer.Tests;

/// <summary>Open results and their user facing descriptions.</summary>
public class OpenResultTests
{
    [Theory]
    [InlineData(OpenOutcome.SwitchedToExistingTab, "Switched to \"Projects\".")]
    [InlineData(OpenOutcome.OpenedNewTab, "Opened \"Projects\" in a new tab.")]
    [InlineData(OpenOutcome.OpenedNewWindow, "Opened \"Projects\" in a new window.")]
    public void Results_have_readable_descriptions(OpenOutcome outcome, string expected)
    {
        var result = new OpenResult(outcome, Path.Combine(Path.GetTempPath(), "Projects"));
        Assert.Equal(expected, ExplorerService.Describe(result));
    }

    [Fact]
    public void Failures_report_their_message()
    {
        var result = new OpenResult(OpenOutcome.Failed, @"C:\x", Message: "boom");
        Assert.Equal("boom", ExplorerService.Describe(result));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Shell_locations_get_friendly_names()
    {
        var result = new OpenResult(OpenOutcome.OpenedNewTab, PathNormalizer.ThisPc);
        Assert.Equal("Opened \"This PC\" in a new tab.", ExplorerService.Describe(result));
    }
}
