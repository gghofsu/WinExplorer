using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class SpecialFolderOrderTests
{
    private const string Home = "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
    private const string ThisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    [Theory]
    [InlineData("shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", 0)] // Home
    [InlineData("shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", 0)] // case does not matter
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 1)] // This PC
    [InlineData("shell:::{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", 2)] // Desktop namespace
    [InlineData("shell:::{374DE290-123F-4565-9164-39C4925E467B}", 3)] // Downloads namespace
    [InlineData(@"C:\Users\someone\Documents", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Rank_recognises_the_fixed_folders(string? location, int? expected)
    {
        if (location is not null && location.StartsWith("C:\\Users") && expected is null)
        {
            Assert.Null(SpecialFolders.Rank(location));
            return;
        }

        Assert.Equal(expected, SpecialFolders.Rank(location));
    }

    [Fact]
    public void Home_ranks_before_this_pc()
    {
        Assert.Equal(0, SpecialFolders.Rank(Home));
        Assert.Equal(1, SpecialFolders.Rank(ThisPc));
    }

    [Fact]
    public void A_normal_folder_is_not_special()
    {
        Assert.False(SpecialFolders.IsSpecial(@"C:\Users\someone\Documents"));
        Assert.Null(SpecialFolders.Describe(@"C:\Users\someone\Documents"));
        Assert.Equal("Home", SpecialFolders.Describe(Home));
        Assert.Equal("This PC", SpecialFolders.Describe(ThisPc));
    }

    [Fact]
    public void Insertion_index_is_the_number_of_higher_priority_tabs()
    {
        var open = new[] { @"C:\Users\someone\Documents", ThisPc };

        // This PC is open, so Home has to go in front of it (position 0)...
        Assert.Equal(0, SpecialFolders.InsertionIndex(open, Home));

        // ...while a normal folder is appended.
        Assert.Equal(-1, SpecialFolders.InsertionIndex(open, @"C:\Users\someone\Pictures"));
    }

    [Fact]
    public void A_lower_priority_folder_goes_before_the_higher_ones()
    {
        var open = new[] { @"C:\Users\someone\Documents" };

        // Nothing is open yet, so This PC is the first tab.
        Assert.Equal(0, SpecialFolders.InsertionIndex(open, ThisPc));
    }

    [Fact]
    public void Nothing_special_open_puts_the_folder_at_the_front()
    {
        var open = new[] { @"C:\one", @"C:\two" };
        Assert.Equal(0, SpecialFolders.InsertionIndex(open, Home));
        Assert.Equal(-1, SpecialFolders.InsertionIndex(open, @"C:\three"));
    }
}
