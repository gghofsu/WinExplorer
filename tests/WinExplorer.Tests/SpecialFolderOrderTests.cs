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
    public void Home_goes_in_front_of_a_normal_folder()
    {
        string[] titles = { "Documents", "Desktop" };
        Assert.Equal(0, SpecialFolders.PositionIn(titles, Home));
    }

    [Fact]
    public void Desktop_goes_after_home_and_before_normal_folders()
    {
        string[] titles = { "Home", "Documents" };
        Assert.Equal(1, SpecialFolders.PositionIn(titles, "shell:::{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"));
    }

    [Fact]
    public void Downloads_goes_after_this_pc_and_desktop()
    {
        string[] titles = { "Home", "This PC", "Desktop", "Pictures" };
        Assert.Equal(3, SpecialFolders.PositionIn(titles, "shell:::{374DE290-123F-4565-9164-39C4925E467B}"));
    }

    [Fact]
    public void A_special_folder_goes_before_a_normal_tab_that_sits_first()
    {
        string[] titles = { "Documents", "Home" };
        Assert.Equal(0, SpecialFolders.PositionIn(titles, ThisPc));
    }

    [Fact]
    public void A_normal_folder_is_never_inserted()
    {
        string[] titles = { "Home", "This PC" };
        Assert.Equal(-1, SpecialFolders.PositionIn(titles, @"C:\Users\someone\Documents"));
    }

    [Fact]
    public void An_empty_strip_puts_the_special_folder_first()
    {
        Assert.Equal(0, SpecialFolders.PositionIn(Array.Empty<string>(), Home));
    }

    [Theory]
    [InlineData("Home", 0)]
    [InlineData("This PC", 1)]
    [InlineData("Desktop", 2)]
    [InlineData("Downloads", 3)]
    [InlineData(" Documents ", null)]
    public void Titles_are_ranked(string title, int? expected)
    {
        Assert.Equal(expected, SpecialFolders.RankOfTitle(title));
    }
}
