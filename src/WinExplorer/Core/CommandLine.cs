namespace WinExplorer.Core;

/// <summary>What the process was asked to do.</summary>
public enum CommandKind
{
    /// <summary>No arguments: show the settings window (start the background helper).</summary>
    None,
    Open,
    OpenNewTab,
    OpenNewWindow,
    DuplicateActiveTab,
    ReopenClosedTab,
    Settings,
    Pause,
    Resume,
    Toggle,
    Exit,
    InstallContextMenu,
    UninstallContextMenu,
    InstallStartup,
    UninstallStartup,
    ToggleStartup,
    Restart,
    Consolidate,
    Diagnose,
    Probe,
    ShowLog,
    Help,
    Version
}

/// <summary>Parsed command line.</summary>
public sealed record CommandLineOptions
{
    public CommandKind Kind { get; init; } = CommandKind.None;
    public string? Path { get; init; }
    public nint Window { get; init; }
    public bool Activate { get; init; } = true;
    public bool Background { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// A tiny, dependency free parser for the switches used by the context menu verbs,
/// shortcuts and the command line. Unknown switches are reported (not thrown) so the
/// UI can tell the user what went wrong.
/// </summary>
public static class CommandLine
{
    public const string DefaultVerb = "open";

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var errors = new List<string>();
        var kind = CommandKind.None;
        string? path = null;
        nint window = 0;
        var activate = true;
        var background = false;
        var kindSet = false;

        void SetKind(CommandKind value)
        {
            if (kindSet)
            {
                if (kind == value)
                    return;

                // "--open <folder> --new-tab" is one command, not two: the explicit
                // --new-tab / --new-window switch refines the plain open request.
                var refineOpen = kind == CommandKind.Open &&
                                 value is CommandKind.OpenNewTab or CommandKind.OpenNewWindow;
                var refinedByOpen = value == CommandKind.Open &&
                                    kind is CommandKind.OpenNewTab or CommandKind.OpenNewWindow;

                if (refineOpen)
                {
                    kind = value;
                    return;
                }

                if (refinedByOpen)
                    return; // keep the more specific command

                errors.Add($"Conflicting commands: '{kind}' and '{value}'.");
            }

            kind = value;
            kindSet = true;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var raw = args[i] ?? string.Empty;
            var arg = raw.Trim();
            if (arg.Length == 0)
                continue;

            // "--flag=value" form
            string? inlineValue = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('/'))
            {
                var eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    inlineValue = arg[(eq + 1)..];
                    arg = arg[..eq];
                }
            }

            string? TakeValue(string name)
            {
                if (inlineValue is not null)
                    return inlineValue;
                if (i + 1 < args.Count && !IsSwitch(args[i + 1]))
                    return args[++i];
                errors.Add($"Missing value for {name}.");
                return null;
            }

            switch (arg.ToLowerInvariant())
            {
                case "open":
                case "--open":
                case "-open":
                    SetKind(CommandKind.Open);
                    break;

                case "--new-tab":
                case "-newtab":
                case "/newtab":
                    SetKind(CommandKind.OpenNewTab);
                    break;

                case "--new-window":
                case "-newwindow":
                    SetKind(CommandKind.OpenNewWindow);
                    break;

                case "--duplicate-tab":
                    SetKind(CommandKind.DuplicateActiveTab);
                    break;

                case "--reopen-closed":
                    SetKind(CommandKind.ReopenClosedTab);
                    break;

                case "--path":
                case "-p":
                    path = TakeValue("--path");
                    break;

                case "--window":
                case "--hwnd":
                {
                    var value = TakeValue("--window");
                    if (value is not null)
                    {
                        window = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? (nint)Convert.ToInt64(value[2..], 16)
                            : long.TryParse(value, out var number) ? (nint)number : 0;
                        if (window == 0)
                            errors.Add($"Invalid window handle '{value}'.");
                    }
                    break;
                }

                case "--no-activate":
                    activate = false;
                    break;

                case "--background":
                case "--tray":
                    background = true;
                    break;

                case "settings":
                case "--settings":
                    SetKind(CommandKind.Settings);
                    break;

                case "pause":
                case "--pause":
                    SetKind(CommandKind.Pause);
                    break;

                case "resume":
                case "--resume":
                    SetKind(CommandKind.Resume);
                    break;

                case "toggle":
                case "--toggle":
                    SetKind(CommandKind.Toggle);
                    break;

                case "exit":
                case "--exit":
                case "quit":
                    SetKind(CommandKind.Exit);
                    break;

                case "install-context-menu":
                case "--install-context-menu":
                case "install-menu":
                    SetKind(CommandKind.InstallContextMenu);
                    break;

                case "uninstall-context-menu":
                case "--uninstall-context-menu":
                case "uninstall-menu":
                    SetKind(CommandKind.UninstallContextMenu);
                    break;

                case "install-startup":
                case "--install-startup":
                    SetKind(CommandKind.InstallStartup);
                    break;

                case "uninstall-startup":
                case "--uninstall-startup":
                    SetKind(CommandKind.UninstallStartup);
                    break;

                case "toggle-startup":
                case "--toggle-startup":
                    SetKind(CommandKind.ToggleStartup);
                    break;

                case "restart":
                case "--restart":
                case "-restart":
                    SetKind(CommandKind.Restart);
                    break;

                case "single-window":
                case "--single-window":
                case "collect-windows":
                case "--collect-windows":
                    SetKind(CommandKind.Consolidate);
                    break;

                case "probe":
                case "--probe":
                    SetKind(CommandKind.Probe);
                    break;

                case "diagnose":
                case "--diagnose":
                case "--doctor":
                case "-diagnose":
                    SetKind(CommandKind.Diagnose);
                    break;

                case "log":
                case "--log":
                    SetKind(CommandKind.ShowLog);
                    break;

                case "help":
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    SetKind(CommandKind.Help);
                    break;

                case "version":
                case "--version":
                case "/version":
                    SetKind(CommandKind.Version);
                    break;

                default:
                    // A bare first argument is the folder to open.
                    if (IsSwitch(arg))
                        errors.Add($"Unknown switch '{arg}'.");
                    else if (path is null)
                        path = raw;
                    else
                        errors.Add($"Unexpected argument '{raw}'.");
                    break;
            }
        }

        // A path given without a verb means "open it".
        if (!kindSet && path is not null)
            kind = CommandKind.Open;

        if (kind is CommandKind.Open or CommandKind.OpenNewTab or CommandKind.OpenNewWindow &&
            string.IsNullOrWhiteSpace(path))
        {
            errors.Add("No folder was given. Use --path <folder>.");
        }

        return new CommandLineOptions
        {
            Kind = kind,
            Path = path,
            Window = window,
            Activate = activate,
            Background = background,
            Errors = errors
        };
    }

    private static bool IsSwitch(string? value) =>
        value is not null && (value.StartsWith("--", StringComparison.Ordinal) || value.StartsWith('/'));

    /// <summary>Text shown by <c>--help</c> and in the README.</summary>
    public const string Usage = """
        WinExplorer - folds every File Explorer window into one tabbed window.

        Usage:
          WinExplorer.exe                         Show the settings window
          WinExplorer.exe --open <folder>         Open a folder as a tab (switches to it if already open)
          WinExplorer.exe --open <folder> --new-tab
                                                  Always create a new tab (duplicates allowed)
          WinExplorer.exe --open <folder> --new-window
                                                  Force a separate window (duplicates allowed)
          WinExplorer.exe --duplicate-tab         Duplicate the active tab
          WinExplorer.exe --reopen-closed         Reopen the last closed folder
          WinExplorer.exe --pause | --resume | --toggle
          WinExplorer.exe --install-context-menu | --uninstall-context-menu
          WinExplorer.exe --install-startup | --uninstall-startup | --toggle-startup
          WinExplorer.exe --single-window        Fold every Explorer window into one tabbed window
          WinExplorer.exe --restart              Restart the helper (recovers a stuck copy)
          WinExplorer.exe --diagnose             Write a diagnostics report and open it
          WinExplorer.exe --probe                List every window, tab and folder the app can see
          WinExplorer.exe --exit
        """;
}
