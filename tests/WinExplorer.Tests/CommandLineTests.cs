using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_means_show_the_window()
    {
        var options = CommandLine.Parse(Array.Empty<string>());
        Assert.Equal(CommandKind.None, options.Kind);
        Assert.False(options.HasErrors);
    }

    [Fact]
    public void Context_menu_command_is_parsed()
    {
        var options = CommandLine.Parse(new[] { "--open", @"C:\Users\me\Documents" });

        Assert.Equal(CommandKind.Open, options.Kind);
        Assert.Equal(@"C:\Users\me\Documents", options.Path);
        Assert.True(options.Activate);
        Assert.False(options.HasErrors);
    }

    [Fact]
    public void Bare_path_opens_it()
    {
        var options = CommandLine.Parse(new[] { @"C:\Temp" });
        Assert.Equal(CommandKind.Open, options.Kind);
        Assert.Equal(@"C:\Temp", options.Path);
    }

    [Fact]
    public void New_tab_and_new_window_switches_are_recognised()
    {
        var tab = CommandLine.Parse(new[] { "--open", @"C:\Temp", "--new-tab" });
        Assert.Equal(CommandKind.OpenNewTab, tab.Kind);

        var window = CommandLine.Parse(new[] { "--open", @"C:\Temp", "--new-window" });
        Assert.Equal(CommandKind.OpenNewWindow, window.Kind);
    }

    [Fact]
    public void Background_and_activation_flags_are_parsed()
    {
        var options = CommandLine.Parse(new[] { "--open", @"C:\Temp", "--no-activate", "--background" });
        Assert.False(options.Activate);
        Assert.True(options.Background);
    }

    [Fact]
    public void Window_handles_accept_decimal_and_hex()
    {
        Assert.Equal((nint)12345, CommandLine.Parse(new[] { "--duplicate-tab", "--hwnd", "12345" }).Window);
        Assert.Equal((nint)0x2A, CommandLine.Parse(new[] { "--duplicate-tab", "--hwnd", "0x2A" }).Window);
    }

    [Fact]
    public void Open_without_a_folder_reports_an_error()
    {
        var options = CommandLine.Parse(new[] { "--open" });
        Assert.True(options.HasErrors);
        Assert.Contains(options.Errors, e => e.Contains("folder"));
    }

    [Fact]
    public void Unknown_switch_is_reported_but_does_not_throw()
    {
        var options = CommandLine.Parse(new[] { "--frobnicate" });
        Assert.True(options.HasErrors);
    }

    [Theory]
    [InlineData("--pause", CommandKind.Pause)]
    [InlineData("--resume", CommandKind.Resume)]
    [InlineData("--toggle", CommandKind.Toggle)]
    [InlineData("--exit", CommandKind.Exit)]
    [InlineData("--settings", CommandKind.Settings)]
    [InlineData("--reopen-closed", CommandKind.ReopenClosedTab)]
    [InlineData("--duplicate-tab", CommandKind.DuplicateActiveTab)]
    [InlineData("--install-context-menu", CommandKind.InstallContextMenu)]
    [InlineData("--uninstall-startup", CommandKind.UninstallStartup)]
    public void Single_word_commands_are_recognised(string arg, CommandKind expected)
    {
        Assert.Equal(expected, CommandLine.Parse(new[] { arg }).Kind);
    }

    [Theory]
    [InlineData("--open")]
    [InlineData("--new-tab")]
    public void New_tab_refines_a_plain_open_in_any_order(string first)
    {
        var args = first == "--open"
            ? new[] { "--open", @"C:\Temp", "--new-tab" }
            : new[] { "--new-tab", "--open", @"C:\Temp" };

        var options = CommandLine.Parse(args);
        Assert.Equal(CommandKind.OpenNewTab, options.Kind);
        Assert.False(options.HasErrors);
    }

    [Fact]
    public void Conflicting_commands_are_reported()
    {
        var options = CommandLine.Parse(new[] { "--pause", "--resume" });
        Assert.True(options.HasErrors);
    }
}
