namespace WinExplorer.Core;

/// <summary>How a folder should be opened.</summary>
public enum OpenMode
{
    /// <summary>Switch to the tab that already shows it, otherwise open a tab in the existing window.</summary>
    ReuseExistingTab = 0,

    /// <summary>Always create a new tab, even when the folder is already open (duplicates allowed).</summary>
    NewTabInExistingWindow = 1,

    /// <summary>Always create a separate window (duplicates allowed).</summary>
    NewWindow = 2
}

/// <summary>What triggered the open. Used for logging and for the duplicate policy.</summary>
public enum OpenOrigin
{
    DoubleClick,
    MiddleClick,
    ContextMenu,
    Shortcut,
    CommandLine,
    WindowMerge,
    ReopenClosed,
    DuplicateTab,
    WinE
}

/// <summary>A single "please show me this folder" request.</summary>
public sealed record OpenRequest
{
    public required string Path { get; init; }

    public OpenMode Mode { get; init; } = OpenMode.ReuseExistingTab;

    /// <summary>Bring the resulting tab (and its window) to the foreground.</summary>
    public bool Activate { get; init; } = true;

    public OpenOrigin Origin { get; init; } = OpenOrigin.CommandLine;

    /// <summary>A window to prefer (e.g. the one that was active when the click happened).</summary>
    public nint PreferredWindow { get; init; }

    /// <summary>
    /// A window that must not be considered as the destination and whose tabs are not
    /// searched for duplicates (used when folding a freshly created window into a tab).
    /// </summary>
    public nint ExcludeWindow { get; init; }

    public static OpenRequest Reuse(string path, OpenOrigin origin, nint window = 0, bool activate = true) =>
        new() { Path = path, Mode = OpenMode.ReuseExistingTab, Origin = origin, PreferredWindow = window, Activate = activate };

    public static OpenRequest NewTab(string path, OpenOrigin origin, nint window = 0, bool activate = true) =>
        new() { Path = path, Mode = OpenMode.NewTabInExistingWindow, Origin = origin, PreferredWindow = window, Activate = activate };

    public static OpenRequest NewWindow(string path, OpenOrigin origin, bool activate = true) =>
        new() { Path = path, Mode = OpenMode.NewWindow, Origin = origin, Activate = activate };
}

/// <summary>Result of an <see cref="OpenRequest"/>.</summary>
public enum OpenOutcome
{
    SwitchedToExistingTab,
    OpenedNewTab,
    OpenedNewWindow,
    Failed
}

/// <summary>Outcome plus the handles involved, for logging and notifications.</summary>
public sealed record OpenResult(
    OpenOutcome Outcome,
    string Path,
    nint Window = 0,
    nint Tab = 0,
    string? Message = null)
{
    public bool Succeeded => Outcome != OpenOutcome.Failed;
}
