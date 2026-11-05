using WinExplorer.Shell;
using Xunit;

namespace WinExplorer.Tests;

public class ExplorerLaunchTests
{
    [Theory]
    [InlineData(@"C:\Projects")]
    [InlineData(@"C:\One folder\Another")]
    [InlineData(@"\\server\share\folder")]
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void New_windows_force_a_new_frame_instead_of_explorers_cross_desktop_reuse(string path)
    {
        Assert.Equal($"/n,\"{path}\"", ExplorerWatcher.BuildNewWindowArguments(path));
    }
}
