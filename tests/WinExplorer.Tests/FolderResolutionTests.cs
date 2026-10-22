using System.IO;
using WinExplorer.Core;
using WinExplorer.Services;
using Xunit;

namespace WinExplorer.Tests;

/// <summary>
/// Resolving "the row the user clicked" into a folder path. This is the piece that decides
/// whether a click becomes a tab request or is replayed to Explorer untouched.
/// </summary>
public class FolderResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinExplorerTests", Guid.NewGuid().ToString("N"));

    public FolderResolutionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Projects"));
        Directory.CreateDirectory(Path.Combine(_root, "Projects", "WinExplorer"));
        File.WriteAllText(Path.Combine(_root, "Projects", "notes.txt"), "hello");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void A_folder_name_resolves_to_its_full_path()
    {
        var result = AppRuntime.ResolveFolder(_root, "Projects");
        Assert.Equal(PathNormalizer.Normalize(Path.Combine(_root, "Projects")), result);
    }

    [Fact]
    public void Name_matching_is_case_insensitive()
    {
        var result = AppRuntime.ResolveFolder(_root, "projects");
        Assert.NotNull(result);
        Assert.EndsWith("Projects", result);
    }

    [Fact]
    public void Files_do_not_resolve_they_must_be_left_to_windows()
    {
        Assert.Null(AppRuntime.ResolveFolder(Path.Combine(_root, "Projects"), "notes.txt"));
    }

    [Fact]
    public void Unknown_names_do_not_resolve()
    {
        Assert.Null(AppRuntime.ResolveFolder(_root, "does-not-exist"));
    }

    [Fact]
    public void Virtual_folders_are_never_resolved()
    {
        Assert.Null(AppRuntime.ResolveFolder(PathNormalizer.ThisPc, "Local Disk (C:)"));
    }

    [Fact]
    public void Missing_input_does_not_resolve()
    {
        Assert.Null(AppRuntime.ResolveFolder(null, "Projects"));
        Assert.Null(AppRuntime.ResolveFolder(_root, null));
        Assert.Null(AppRuntime.ResolveFolder(_root, "   "));
    }

    [Fact]
    public void Path_traversal_in_a_display_name_is_ignored()
    {
        Assert.Null(AppRuntime.ResolveFolder(Path.Combine(_root, "Projects"), "..\\..\\Windows"));
    }

    [Theory]
    [InlineData(OpenOutcome.SwitchedToExistingTab, "Switched to \"Projects\".")]
    [InlineData(OpenOutcome.OpenedNewTab, "Opened \"Projects\" in a new tab.")]
    [InlineData(OpenOutcome.OpenedNewWindow, "Opened \"Projects\" in a new window.")]
    public void Results_have_readable_descriptions(OpenOutcome outcome, string expected)
    {
        var result = new OpenResult(outcome, Path.Combine(_root, "Projects"));
        Assert.Equal(expected, ExplorerService.Describe(result));
    }

    [Fact]
    public void Failures_report_their_message()
    {
        var result = new OpenResult(OpenOutcome.Failed, @"C:\x", Message: "boom");
        Assert.Equal("boom", ExplorerService.Describe(result));
        Assert.False(result.Succeeded);
    }
}
