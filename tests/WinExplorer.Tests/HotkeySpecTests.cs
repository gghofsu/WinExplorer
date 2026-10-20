using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class HotkeySpecTests
{
    [Theory]
    [InlineData("Ctrl+Shift+D", true, true, false, false, "D")]
    [InlineData("ctrl + alt + p", true, false, true, false, "P")]
    [InlineData("Win+E", false, false, false, true, "E")]
    [InlineData("Alt+F5", false, false, true, false, "F5")]
    [InlineData("shift+ctrl+oemcomma", true, true, false, false, "OemComma")]
    public void TryParse_reads_modifiers_and_keys(string text, bool ctrl, bool shift, bool alt, bool win, string key)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec, out var error));
        Assert.Null(error);
        Assert.Equal(ctrl, spec.Ctrl);
        Assert.Equal(shift, spec.Shift);
        Assert.Equal(alt, spec.Alt);
        Assert.Equal(win, spec.Win);
        Assert.Equal(key, spec.Key);
    }

    [Theory]
    [InlineData("D")]           // no modifier
    [InlineData("Ctrl+Shift")]  // modifiers only
    [InlineData("Ctrl+Nope")]   // unknown key
    [InlineData("Ctrl+A+B")]    // two keys
    public void TryParse_rejects_invalid_shortcuts(string text)
    {
        Assert.False(HotkeySpec.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Empty_shortcut_is_valid_and_means_not_assigned()
    {
        Assert.True(HotkeySpec.TryParse(string.Empty, out var spec, out _));
        Assert.True(spec.IsEmpty);
        Assert.Equal("Not set", spec.ToString());
    }

    [Fact]
    public void ToString_round_trips()
    {
        var spec = new HotkeySpec { Ctrl = true, Alt = true, Key = "K" };
        Assert.Equal("Ctrl+Alt+K", spec.ToString());

        Assert.True(HotkeySpec.TryParse(spec.ToString(), out var parsed, out _));
        Assert.True(spec.SameChord(parsed));
    }

    [Fact]
    public void Punctuation_keys_get_friendly_names()
    {
        var spec = new HotkeySpec { Alt = true, Key = "OemComma" };
        Assert.Equal("Alt+,", spec.ToString());
    }

    [Fact]
    public void SameChord_ignores_casing_but_not_modifiers()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+D", out var a, out _));
        Assert.True(HotkeySpec.TryParse("ctrl+d", out var b, out _));
        Assert.True(a.SameChord(b));

        Assert.True(HotkeySpec.TryParse("Ctrl+Shift+D", out var c, out _));
        Assert.False(a.SameChord(c));
    }
}
