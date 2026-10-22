namespace WinExplorer.Core;

/// <summary>What to do when the folder is already displayed in some tab.</summary>
public enum AlreadyOpenBehavior
{
    /// <summary>Jump to the tab that already shows the folder (default).</summary>
    SwitchToExistingTab = 0,

    /// <summary>Open another tab anyway (duplicates allowed).</summary>
    OpenAnotherTab = 1
}

/// <summary>Settings persisted to <c>%APPDATA%\WinExplorer\settings.json</c>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;

    // ---- Core behaviour -------------------------------------------------

    /// <summary>Master switch for everything the app does to Explorer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Repeated opens of the same folder: switch to the tab or duplicate it.</summary>
    public AlreadyOpenBehavior WhenAlreadyOpen { get; set; } = AlreadyOpenBehavior.SwitchToExistingTab;

    /// <summary>
    /// Keep exactly one File Explorer window. Every new window Explorer creates - the
    /// desktop, Start, the taskbar, Win+E, another app - is reopened as a tab of the window
    /// that is already there, unless the user explicitly asked for a window
    /// (Ctrl+Shift while it opens).
    /// </summary>
    public bool SingleWindowMode { get; set; } = true;

    /// <summary>
    /// Keep Home, This PC, Desktop and Downloads at the left of the tab strip, in that order
    /// (they are inserted in front of every other tab). On by default.
    /// </summary>
    public bool KeepSpecialFoldersFirst { get; set; } = true;

    /// <summary>Treat Win+E as "open a tab in the existing window".</summary>
    public bool TakeOverWinE { get; set; } = false;

    // ---- Integration ----------------------------------------------------

    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool ContextMenuInstalled { get; set; }

    public bool ShowNotifications { get; set; }

    /// <summary>Show a tray balloon the first time the app runs.</summary>
    public bool FirstRunCompleted { get; set; }

    // ---- Shortcuts ------------------------------------------------------

    public string DuplicateTabHotkey { get; set; } = HotkeySpec.DuplicateTabDefault.ToString();

    public string ReopenClosedHotkey { get; set; } = HotkeySpec.ReopenClosedDefault.ToString();

    public string TogglePauseHotkey { get; set; } = HotkeySpec.TogglePauseDefault.ToString();

    // ---- Misc -----------------------------------------------------------

    /// <summary>Folders (normalized) that should never be redirected.</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Enabled = Enabled,
        WhenAlreadyOpen = WhenAlreadyOpen,
        SingleWindowMode = SingleWindowMode,
        KeepSpecialFoldersFirst = KeepSpecialFoldersFirst,
        TakeOverWinE = TakeOverWinE,
        StartWithWindows = StartWithWindows,
        StartMinimized = StartMinimized,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
        ContextMenuInstalled = ContextMenuInstalled,
        ShowNotifications = ShowNotifications,
        FirstRunCompleted = FirstRunCompleted,
        DuplicateTabHotkey = DuplicateTabHotkey,
        ReopenClosedHotkey = ReopenClosedHotkey,
        TogglePauseHotkey = TogglePauseHotkey,
        ExcludedPaths = new List<string>(ExcludedPaths)
    };

    /// <summary>True when the location was explicitly excluded by the user.</summary>
    public bool IsExcluded(string normalizedPath) =>
        ExcludedPaths.Any(p => string.Equals(PathNormalizer.Normalize(p), normalizedPath,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>The three configurable shortcuts, resolved.</summary>
    public (HotkeySpec Duplicate, HotkeySpec Reopen, HotkeySpec TogglePause) GetHotkeys()
    {
        HotkeySpec Parse(string text, HotkeySpec fallback) =>
            HotkeySpec.TryParse(text, out var spec, out _) ? spec : fallback;

        return (
            Parse(DuplicateTabHotkey, HotkeySpec.DuplicateTabDefault),
            Parse(ReopenClosedHotkey, HotkeySpec.ReopenClosedDefault),
            Parse(TogglePauseHotkey, HotkeySpec.TogglePauseDefault));
    }
}
