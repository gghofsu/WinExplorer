using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Documents\", @"C:\Users\me\Documents")]
    [InlineData(@"C:/Users/me/Documents", @"C:\Users\me\Documents")]
    [InlineData("/Users/me", @"\Users\me")]
    [InlineData("file:///C:/Users/me/My%20Folder", @"C:\Users\me\My Folder")]
    [InlineData("  \"C:\\Temp\"  ", @"C:\Temp")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_produces_a_comparable_path(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("shell::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void Normalize_understands_shell_namespace_shorthand(string input)
    {
        Assert.Equal(PathNormalizer.ThisPc, PathNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_keeps_hash_characters_in_file_urls()
    {
        var result = PathNormalizer.Normalize("file:///C:/Temp/100%23issues");
        Assert.Equal(@"C:\Temp\100#issues", result);
    }

    [Fact]
    public void Normalize_decodes_utf8_escapes_in_file_urls()
    {
        // Explorer percent-escapes non-ASCII names as UTF-8 (é = %C3%A9).
        Assert.Equal(@"C:\Temp\café", PathNormalizer.Normalize("file:///C:/Temp/caf%C3%A9"));
    }

    [Fact]
    public void Normalize_keeps_the_unc_host_of_file_urls()
    {
        Assert.Equal(@"\\server\share\folder",
            PathNormalizer.Normalize("file://server/share/folder"));
    }

    [Fact]
    public void Normalize_handles_empty_input()
    {
        Assert.Equal(string.Empty, PathNormalizer.Normalize(null));
        Assert.Equal(string.Empty, PathNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_expands_environment_variables()
    {
        var result = PathNormalizer.Normalize(@"%USERPROFILE%\Desktop");
        Assert.DoesNotContain("%", result);
        Assert.EndsWith(@"\Desktop", result);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents", "Documents")]
    [InlineData(@"C:\", "C:")]
    [InlineData(PathNormalizer.ThisPc, "This PC")]
    [InlineData(PathNormalizer.Home, "Home")]
    public void ToDisplayName_is_readable(string path, string expected)
    {
        Assert.Equal(expected, PathNormalizer.ToDisplayName(path));
    }

    [Fact]
    public void IsShellNamespace_detects_virtual_folders()
    {
        Assert.True(PathNormalizer.IsShellNamespace(PathNormalizer.ThisPc));
        Assert.False(PathNormalizer.IsShellNamespace(@"C:\Temp"));
    }
}
