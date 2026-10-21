using System.IO;
using WinExplorer.Core;
using Xunit;

namespace WinExplorer.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinExplorerTests", Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Missing_file_produces_defaults()
    {
        var store = new SettingsStore(SettingsFile);
        var settings = store.Load();

        Assert.True(settings.Enabled);
        Assert.True(settings.InterceptDoubleClick);
        Assert.Equal(AlreadyOpenBehavior.SwitchToExistingTab, settings.WhenAlreadyOpen);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var store = new SettingsStore(SettingsFile);
        var settings = store.Load();
        settings.InterceptMiddleClick = false;
        settings.WhenAlreadyOpen = AlreadyOpenBehavior.OpenAnotherTab;
        settings.ExcludedPaths.Add(@"C:\Temp");
        settings.DuplicateTabHotkey = "Ctrl+Alt+D";
        store.Save(settings);

        var reloaded = new SettingsStore(SettingsFile).Load();

        Assert.False(reloaded.InterceptMiddleClick);
        Assert.Equal(AlreadyOpenBehavior.OpenAnotherTab, reloaded.WhenAlreadyOpen);
        Assert.Contains(@"C:\Temp", reloaded.ExcludedPaths);
        Assert.Equal("Ctrl+Alt+D", reloaded.DuplicateTabHotkey);
    }

    [Fact]
    public void Enums_are_stored_as_readable_text()
    {
        var store = new SettingsStore(SettingsFile);
        var settings = store.Load();
        settings.TargetWindow = TargetWindowPolicy.LargestWindow;
        store.Save(settings);

        Assert.Contains("LargestWindow", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void Corrupt_json_falls_back_to_defaults_and_keeps_the_bad_file()
    {
        File.WriteAllText(SettingsFile, "{ this is not json ");

        var store = new SettingsStore(SettingsFile);
        var settings = store.Load();

        Assert.True(settings.Enabled);
        Assert.True(File.Exists(Path.ChangeExtension(SettingsFile, ".bad.json")));
    }

    [Fact]
    public void Sanitize_cleans_excluded_paths()
    {
        var settings = new AppSettings
        {
            ExcludedPaths = new List<string> { @"C:\Temp\", "", @"c:\temp", @"D:\Games" }
        };

        var sanitized = SettingsStore.Sanitize(settings);

        Assert.Equal(2, sanitized.ExcludedPaths.Count);
        Assert.Contains(@"C:\Temp", sanitized.ExcludedPaths);
    }

    [Fact]
    public void IsExcluded_matches_normalized_paths()
    {
        var settings = new AppSettings { ExcludedPaths = new List<string> { @"C:\Temp\" } };

        Assert.True(settings.IsExcluded(@"c:\temp"));
        Assert.False(settings.IsExcluded(@"C:\Other"));
    }

    [Fact]
    public void Hotkeys_fall_back_to_defaults_when_invalid()
    {
        var settings = new AppSettings { DuplicateTabHotkey = "nonsense" };
        var (duplicate, _, _) = settings.GetHotkeys();

        Assert.True(duplicate.SameChord(HotkeySpec.DuplicateTabDefault));
    }

    [Fact]
    public void Clone_is_independent()
    {
        var settings = new AppSettings { ExcludedPaths = new List<string> { @"C:\Temp" } };
        var clone = settings.Clone();

        clone.ExcludedPaths.Add(@"C:\Other");
        clone.Enabled = false;

        Assert.Single(settings.ExcludedPaths);
        Assert.True(settings.Enabled);
    }
}
