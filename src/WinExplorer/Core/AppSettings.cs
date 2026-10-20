namespace WinExplorer.Core;

/// <summary>What to do when the folder is already displayed in some tab.</summary>
public enum AlreadyOpenBehavior
{
    /// <summary>Jump to the tab that already shows the folder (default).</summary>
    SwitchToExistingTab = 0,

    /// <summary>Open another tab anyway (duplicates allowed).</summary>
    OpenAnotherTab = 1
}

/// <summary>Which window receives the new tab.</summary>
public enum TargetWindowPolicy
{
    /// <summary>The File Explorer window that had focus when the folder was opened.</summary>
    ForegroundWindow = 0,

    /// <summary>The most recently used File Explorer window.</summary>
    LastUsedWindow = 1,

    /// <summary>The window that already holds the most tabs (keeps one window growing).</summary>
    LargestWindow = 2
}

/// <summary>Settings persisted to <c>%APPDATA%\WinExplorer\settings.json</c>.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // ---- Core behaviour -------------------------------------------------

    /// <summary>Master switch for everything the app does to Explorer.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Intercept double-clicks on folders inside a File Explorer window.</summary>
    public bool InterceptDoubleClick { get; set; } = true;

    /// <summary>Intercept middle-clicks (Windows would open a background tab).</summary>
    public bool InterceptMiddleClick { get; set; } = true;

    /// <summary>Bring a newly created tab to the front.</summary>
    public bool ActivateNewTab { get; set; } = true;

    /// <summary>Repeated opens of the same folder: switch to the tab or duplicate it.</summary>
    public AlreadyOpenBehavior WhenAlreadyOpen { get; set; } = AlreadyOpenBehavior.SwitchToExistingTab;

    /// <summary>Search every File Explorer window for an open copy, not just the target one.</summary>
    public bool DedupeAcrossAllWindows { get; set; } = true;

    /// <summary>Create new tabs in the window that last had focus.</summary>
    public TargetWindowPolicy TargetWindow { get; set; } = TargetWindowPolicy.ForegroundWindow;

    // ---- Windows --------------------------------------------------------

    /// <summary>Fold newly appearing Explorer windows into tabs.</summary>
    public bool MergeNewWindows { get; set; } = true;

    /// <summary>Do not merge windows whose location is a virtual shell folder.</summary>
    public bool KeepVirtualFoldersAsWindows { get; set; } = true;

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
        InterceptDoubleClick = InterceptDoubleClick,
        InterceptMiddleClick = InterceptMiddleClick,
        ActivateNewTab = ActivateNewTab,
        WhenAlreadyOpen = WhenAlreadyOpen,
        DedupeAcrossAllWindows = DedupeAcrossAllWindows,
        TargetWindow = TargetWindow,
        MergeNewWindows = MergeNewWindows,
        KeepVirtualFoldersAsWindows = KeepVirtualFoldersAsWindows,
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
