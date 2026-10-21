using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WinExplorer.Core;
using WinExplorer.Services;
using WinExplorer.UI;

namespace WinExplorer;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private Logger? _log;
    private SettingsStore? _store;
    private AppRuntime? _runtime;
    private IpcServer? _ipc;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private bool _ownsInstance;
    private bool _trayHintShown;

    /// <summary>True once WinExplorer is on its way out (the window may stop hiding then).</summary>
    public bool IsExiting { get; private set; }

    public AppSettings Settings => _runtime?.Settings ?? _store?.Current ?? new AppSettings();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                _log?.Error("Unhandled exception", exception);
        };

        _log = new Logger();
        _store = new SettingsStore();
        _store.Load();

        var options = CommandLine.Parse(e.Args);
        _log.Info($"Started with: '{string.Join(' ', e.Args)}' (command: {options.Kind}).");

        if (options.HasErrors)
            _log.Warn($"Command line problems: {string.Join("; ", options.Errors)}");

        switch (options.Kind)
        {
            case CommandKind.Help:
                ConsoleHelper.WriteLine(CommandLine.Usage);
                Shutdown(0);
                return;

            case CommandKind.Version:
                ConsoleHelper.WriteLine($"WinExplorer {Version}");
                Shutdown(0);
                return;

            // These only touch per-user registry keys / open a file, so they never need the
            // background helper (and must work while it is running).
            case CommandKind.InstallContextMenu:
            case CommandKind.UninstallContextMenu:
            case CommandKind.InstallStartup:
            case CommandKind.UninstallStartup:
            case CommandKind.ToggleStartup:
            case CommandKind.ShowLog:
                RunLocalCommand(options);
                Shutdown(0);
                return;
        }

        _instanceMutex = new Mutex(true, AppPaths.MutexName, out var createdNew);
        _ownsInstance = createdNew;

        if (!_ownsInstance)
        {
            // Another WinExplorer owns the hooks: forward the request and exit.
            ForwardToRunningInstance(options);
            return;
        }

        StartHost(options);
    }

    /// <summary>Starts the background helper, the tray icon and optionally the window.</summary>
    private void StartHost(CommandLineOptions options)
    {
        _runtime = new AppRuntime(_log!, _store!);
        _runtime.Activity += (_, message) =>
        {
            if (!Settings.ShowNotifications)
                return;
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(message));
        };
        _runtime.SettingsChanged += (_, settings) => Dispatcher.BeginInvoke(() =>
        {
            _tray?.UpdateState(settings.Enabled);
            _tray?.UpdateStartupState(settings.StartWithWindows);
            _viewModel?.RefreshFromSettings();
        });

        _runtime.Start();
        _tray = CreateTrayIcon();
        _tray.UpdateState(Settings.Enabled);

        _ipc = new IpcServer(_log!, HandleIpcAsync);
        _ipc.Start();

        SyncIntegrationState();

        if (options.Kind == CommandKind.Exit)
        {
            ExitApplication();
            return;
        }

        var openKind = options.Kind is CommandKind.Open or CommandKind.OpenNewTab or CommandKind.OpenNewWindow;
        if (openKind)
        {
            var mode = options.Kind switch
            {
                CommandKind.OpenNewTab => "newtab",
                CommandKind.OpenNewWindow => "newwindow",
                _ => "reuse"
            };

            _ = Task.Run(async () =>
            {
                var response = await _runtime.HandleIpcAsync(IpcClient.FromCommand(options, mode)).ConfigureAwait(false);
                if (!response.Ok)
                    _log!.Warn($"Open request failed: {response.Message}");
            });
        }
        else if (options.Kind is CommandKind.DuplicateActiveTab or CommandKind.ReopenClosedTab
                 or CommandKind.Toggle or CommandKind.Pause or CommandKind.Resume)
        {
            _ = Task.Run(async () =>
            {
                var response = await _runtime.HandleIpcAsync(IpcClient.FromCommand(options)).ConfigureAwait(false);
                _log!.Debug($"Background command '{options.Kind}' -> {response.Outcome ?? response.Message}");
            });
        }

        var firstRun = !Settings.FirstRunCompleted;
        var showWindow = options.Kind is CommandKind.None or CommandKind.Settings;

        // "Start with Windows" launches us with --background: no window on that path.
        if (options.Background && !firstRun)
            showWindow = false;

        if (showWindow)
            ShowSettingsWindow();
        else if (openKind)
            NotifyHiddenToTray();

        if (firstRun)
        {
            Settings.FirstRunCompleted = true;
            _store!.Save(Settings);
            ShowSettingsWindow();
        }
    }

    private TrayIcon CreateTrayIcon() => new(
        _log!,
        isStartupEnabled: () => _runtime!.Startup.IsEnabled(),
        openSettings: () => Dispatcher.BeginInvoke(ShowSettingsWindow),
        toggleEnabled: () =>
        {
            var enabled = _runtime!.ToggleEnabled();
            Dispatcher.BeginInvoke(() => _tray?.UpdateState(enabled));
        },
        toggleStartup: () => Dispatcher.BeginInvoke(ToggleStartup),
        duplicateTab: () => _ = _runtime!.DuplicateActiveTabAsync(),
        reopenClosed: () => _ = _runtime!.ReopenLastClosedAsync(),
        openLog: OpenLog,
        exit: () => Dispatcher.BeginInvoke(ExitApplication));

    /// <summary>Adds or removes the "start when I sign in" entry from the tray menu.</summary>
    private void ToggleStartup()
    {
        var startup = _runtime!.Startup;
        var enabled = !startup.IsEnabled();

        if (!startup.SetEnabled(enabled))
        {
            MessageBox.Show(
                "Windows would not let WinExplorer change the startup entry.\n\n" +
                "The registry key is HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run.",
                "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _runtime.Settings.StartWithWindows = enabled;
        _store!.Save(_runtime.Settings);
        _tray?.UpdateStartupState(enabled);
        _viewModel?.RefreshFromSettings();
    }

    /// <summary>Keeps the registry in sync with what settings.json claims.</summary>
    private void SyncIntegrationState()
    {
        try
        {
            var shell = _runtime!.Shell;

            // Refresh the verbs (the executable may have moved) or remove them.
            if (Settings.ContextMenuInstalled)
                shell.Install();
            else if (shell.IsInstalled)
                shell.Uninstall();

            // Only remove what we added: the user may have disabled the entry from
            // Task Manager, and that choice must survive a restart.
            if (!Settings.StartWithWindows && _runtime.Startup.IsEnabled())
                _runtime.Startup.SetEnabled(false);
        }
        catch (Exception ex)
        {
            _log?.Error("Could not synchronise the Windows integration", ex);
        }
    }

    /// <summary>
    /// Runs the commands that do not involve File Explorer (registry edits, opening the log)
    /// and reports the result on the console.
    /// </summary>
    private void RunLocalCommand(CommandLineOptions options)
    {
        var shell = new ShellIntegration(_log!);
        var startup = new StartupRegistration(_log!);
        var settings = _store!.Current;

        switch (options.Kind)
        {
            case CommandKind.InstallContextMenu:
            {
                var ok = shell.Install();
                settings.ContextMenuInstalled = ok;
                _store.Save(settings);
                ConsoleHelper.WriteLine(ok
                    ? "Folder context menu installed. Double-click integration: " +
                      (shell.SetAsDefaultFolderHandler(true) ? "on" : "could not be enabled") + "."
                    : "Windows would not let WinExplorer change the context menu.");
                break;
            }

            case CommandKind.UninstallContextMenu:
            {
                var ok = shell.Uninstall();
                settings.ContextMenuInstalled = false;
                _store.Save(settings);
                ConsoleHelper.WriteLine(ok
                    ? "Folder context menu removed and the previous folder handler restored."
                    : "Windows would not let WinExplorer change the context menu.");
                break;
            }

            case CommandKind.InstallStartup:
            {
                var ok = startup.SetEnabled(true);
                settings.StartWithWindows = ok;
                _store.Save(settings);
                ConsoleHelper.WriteLine(ok
                    ? "WinExplorer will start with Windows."
                    : "Windows would not let WinExplorer change the startup entry.");
                break;
            }

            case CommandKind.UninstallStartup:
            {
                var ok = startup.SetEnabled(false);
                settings.StartWithWindows = false;
                _store.Save(settings);
                ConsoleHelper.WriteLine(ok
                    ? "WinExplorer will no longer start with Windows."
                    : "Windows would not let WinExplorer change the startup entry.");
                break;
            }

            case CommandKind.ToggleStartup:
            {
                var enabled = !startup.IsEnabled();
                var ok = startup.SetEnabled(enabled);
                settings.StartWithWindows = ok && enabled;
                _store.Save(settings);
                ConsoleHelper.WriteLine(ok
                    ? (enabled
                        ? "WinExplorer will start with Windows."
                        : "WinExplorer will no longer start with Windows.")
                    : "Windows would not let WinExplorer change the startup entry.");
                break;
            }

            case CommandKind.ShowLog:
                OpenLog();
                break;
        }
    }

    private async Task<IpcResponse> HandleIpcAsync(IpcRequest request)
    {
        switch (request.Kind)
        {
            case "settings":
                await Dispatcher.InvokeAsync(ShowSettingsWindow, DispatcherPriority.Normal);
                return new IpcResponse { Ok = true, Outcome = "shown" };

            case "exit":
                await Dispatcher.InvokeAsync(ExitApplication, DispatcherPriority.Normal);
                return new IpcResponse { Ok = true, Outcome = "exiting" };

            default:
                return await _runtime!.HandleIpcAsync(request).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a command to the already running instance, starting nothing locally.</summary>
    private void ForwardToRunningInstance(CommandLineOptions options)
    {
        _ = Task.Run(async () =>
        {
            var request = IpcClient.FromCommand(options, options.Kind switch
            {
                CommandKind.OpenNewTab => "newtab",
                CommandKind.OpenNewWindow => "newwindow",
                _ => null
            });

            IpcResponse? response = null;
            for (var attempt = 0; attempt < 6 && response is null; attempt++)
            {
                response = await IpcClient.SendAsync(request, 1500).ConfigureAwait(false);
                if (response is null)
                    await Task.Delay(300).ConfigureAwait(false);
            }

            if (response is null)
            {
                _log?.Warn("The running instance did not answer; falling back to a local action.");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "WinExplorer is already running but did not answer. Try again, or exit it from the notification area.",
                        "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ExitApplication();
                });
                return;
            }

            if (!response.Ok)
            {
                _log?.Warn($"Running instance refused the request: {response.Message}");
                await Dispatcher.InvokeAsync(() => MessageBox.Show(
                    $"WinExplorer could not open the folder.\n\n{response.Message}",
                    "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning));
            }

            await Dispatcher.InvokeAsync(ExitApplication);
        });
    }

    // ------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------

    public void ShowSettingsWindow()
    {
        if (_runtime is null)
            return;

        if (_window is null)
        {
            _viewModel = new MainViewModel(_runtime);
            _window = new MainWindow(this, _viewModel);
            _window.Closed += (_, _) => _window = null;
        }

        if (!_window.IsVisible)
            _window.Show();

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void NotifyHiddenToTray()
    {
        if (_trayHintShown)
            return;

        _trayHintShown = true;
        _tray?.ShowBalloon("WinExplorer keeps running here. Double-click the tray icon to open settings.");
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not open the log: {ex.Message}");
        }
    }

    public void ExitApplication()
    {
        if (IsExiting)
            return;

        IsExiting = true;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;

        try { _ipc?.Dispose(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _runtime?.Dispose(); } catch { /* ignore */ }
        _log?.Info("Goodbye.");

        if (_instanceMutex is not null)
        {
            if (_ownsInstance)
            {
                try { _instanceMutex.ReleaseMutex(); } catch { /* ignore */ }
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("UI exception", e.Exception);

        // Keep running: a broken settings window must not kill the Explorer integration.
        e.Handled = true;
        MessageBox.Show(
            $"WinExplorer hit a problem but is still running.\n\n{e.Exception.Message}\n\nDetails were written to:\n{AppPaths.LogFile}",
            "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string Version =>
        typeof(App).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";
}
