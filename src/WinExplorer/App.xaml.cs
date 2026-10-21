using System.Diagnostics;
using System.IO;
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

            case CommandKind.Diagnose:
                RunDiagnostics();
                Shutdown(0);
                return;

            case CommandKind.Probe:
                RunProbe();
                Shutdown(0);
                return;
        }

        if (options.Kind == CommandKind.Restart)
        {
            // Replace the running copy instead of asking it to do something.
            RunRestart(options);
            return;
        }

        if (TryAcquireInstance(TimeSpan.Zero))
        {
            StartHost(options);
            return;
        }

        // Another WinExplorer owns the hooks: forward the request and exit.
        ForwardToRunningInstance(options);
    }

    /// <summary>Starts the background helper, the tray icon and optionally the window.</summary>
    /// <remarks>
    /// Every stage is isolated: if one part fails (a hook, the tray icon) the process must
    /// still answer on the IPC pipe, otherwise a half-started copy holds the single-instance
    /// mutex and every later launch only sees "already running but did not answer".
    /// </remarks>
    private void StartHost(CommandLineOptions options)
    {
        _log!.Info($"Startup stage 1/4: this process owns the instance (pid {Environment.ProcessId}).");

        // The pipe comes first so the instance is reachable from the very beginning.
        try
        {
            _ipc = new IpcServer(_log, HandleIpcAsync);
            _ipc.Start();
            _log.Info("Startup stage 1/4: IPC server listening.");
        }
        catch (Exception ex)
        {
            _log.Error("The IPC server could not start", ex);
        }

        try
        {
            _runtime = new AppRuntime(_log, _store!);
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
            _log.Info("Startup stage 2/4: background helper started.");
        }
        catch (Exception ex)
        {
            _log.Error("The background helper could not start", ex);
            _runtime?.Dispose();
            _runtime = null;

            // Without the helper there is nothing useful to keep running; letting the process
            // die frees the mutex so the next launch is clean.
            MessageBox.Show(
                "WinExplorer could not start.\n\n" + ex.Message +
                $"\n\nDetails were written to:\n{AppPaths.LogFile}",
                "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
            return;
        }

        try
        {
            _tray = CreateTrayIcon();
            _tray.UpdateState(Settings.Enabled);
            _log.Info("Startup stage 3/4: tray icon ready.");
        }
        catch (Exception ex)
        {
            _log.Error("The notification area icon could not be created", ex);
            _tray = null;
        }

        try
        {
            SyncIntegrationState();
        }
        catch (Exception ex)
        {
            _log.Error("The Windows integration could not be synchronised", ex);
        }

        _log.Info("Startup stage 4/4: ready.");

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
                var response = await _runtime!.HandleIpcAsync(IpcClient.FromCommand(options, mode)).ConfigureAwait(false);
                if (!response.Ok)
                    _log!.Warn($"Open request failed: {response.Message}");
            });
        }
        else if (options.Kind is CommandKind.DuplicateActiveTab or CommandKind.ReopenClosedTab
                 or CommandKind.Toggle or CommandKind.Pause or CommandKind.Resume
                 or CommandKind.Consolidate)
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
        collectWindows: () => _ = CollectWindowsAsync(),
        openLog: OpenLog,
        restart: Restart,
        exit: () => Dispatcher.BeginInvoke(ExitApplication));

    /// <summary>Folds every other Explorer window into the current one (tray menu).</summary>
    private async Task CollectWindowsAsync()
    {
        try
        {
            var folded = await _runtime!.ConsolidateWindowsAsync().ConfigureAwait(false);
            if (folded == 0)
                _tray?.ShowBalloon("There is only one File Explorer window open.");
        }
        catch (Exception ex)
        {
            _log?.Error("Collecting the Explorer windows failed", ex);
        }
    }

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
        // "ping" is answered from the IPC thread, so a busy (or stuck) UI thread still shows
        // up as "running" instead of looking dead.
        switch (request.Kind)
        {
            case "ping":
                return new IpcResponse
                {
                    Ok = true,
                    Outcome = _runtime is null ? "pong (helper unavailable)" : "pong",
                    Message = $"pid {Environment.ProcessId}"
                };

            case "settings":
            {
                var shown = await RunOnUiThreadAsync(ShowSettingsWindow,
                    Math.Min(request.TimeoutMs, 10000)).ConfigureAwait(false);
                return shown
                    ? new IpcResponse { Ok = true, Outcome = "shown", Message = $"pid {Environment.ProcessId}" }
                    : new IpcResponse { Ok = false, Message = "The settings window did not open in time." };
            }

            case "exit":
            {
                var exiting = await RunOnUiThreadAsync(ExitApplication, 2000).ConfigureAwait(false);
                if (!exiting)
                {
                    _log?.Warn("The UI thread did not shut down in time; ending the process.");
                    Environment.Exit(0);
                }

                return new IpcResponse { Ok = true, Outcome = "exiting" };
            }

            default:
                if (_runtime is null)
                    return new IpcResponse { Ok = false, Message = "The background helper is not running." };

                return await _runtime.HandleIpcAsync(request).ConfigureAwait(false);
        }
    }

    /// <summary>Runs an action on the UI thread and reports whether it finished in time.</summary>
    private async Task<bool> RunOnUiThreadAsync(Action action, int timeoutMs)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted)
                return false;

            var operation = Dispatcher.InvokeAsync(action, DispatcherPriority.Send);
            var finished = await Task.WhenAny(operation.Task, Task.Delay(Math.Max(250, timeoutMs)))
                .ConfigureAwait(false);

            if (finished != operation.Task)
                return false;

            await operation.Task.ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error("A UI action failed", ex);
            return false;
        }
    }

    /// <summary>Sends a command to the already running instance, starting nothing locally.</summary>
    private void ForwardToRunningInstance(CommandLineOptions options)
    {
        _ = Task.Run(async () =>
        {
            IpcAttempt attempt = new(null, "not tried");
            var request = IpcClient.FromCommand(options, options.Kind switch
            {
                CommandKind.OpenNewTab => "newtab",
                CommandKind.OpenNewWindow => "newwindow",
                _ => null
            });

            _log?.Info($"Another instance owns the single-instance mutex; forwarding '{options.Kind}'.");

            // An installer or uninstaller asks for --exit and waits for us; never block it
            // behind a dialog or a long timeout.
            if (options.Kind == CommandKind.Exit)
                await SendWithRetriesAsync(request, deadlineMs: 4000).ConfigureAwait(false);
            else
                attempt = await SendWithRetriesAsync(request).ConfigureAwait(false);

            if (options.Kind == CommandKind.Exit)
            {
                await Dispatcher.InvokeAsync(ExitApplication);
                return;
            }

            if (!attempt.Answered)
            {
                _log?.Warn($"The running instance did not answer: {attempt.Error}");

                // Nothing is listening although we do not own the instance: either a copy is
                // still starting up (single-file extraction is slow) or it is wedged. Take
                // over when it is gone, ask before replacing a wedged copy.
                if (WinExplorerProcesses.Others().Count == 0)
                {
                    _log?.Info("No other WinExplorer process is alive; taking over.");
                    await Dispatcher.InvokeAsync(() => TakeOver(options));
                    return;
                }

                var restart = await Dispatcher.InvokeAsync(() => MessageBox.Show(
                    "WinExplorer is already running but is not responding.\n\n" +
                    "Restart it? This closes the unresponsive copy and starts a fresh one. " +
                    "Your settings are kept.",
                    "WinExplorer", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.Yes));

                if (restart == MessageBoxResult.Yes)
                {
                    await Dispatcher.InvokeAsync(() => TakeOver(options));
                    return;
                }

                await Dispatcher.InvokeAsync(ExitApplication);
                return;
            }

            var response = attempt.Response!;
            if (!response.Ok)
            {
                _log?.Warn($"Running instance refused the request: {response.Message}");
                await Dispatcher.InvokeAsync(() => MessageBox.Show(
                    $"WinExplorer could not open the folder.\n\n{response.Message}",
                    "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else if (options.Kind is CommandKind.None or CommandKind.Settings)
            {
                _log?.Info($"The running instance answered: {response.Outcome}");
            }

            await Dispatcher.InvokeAsync(ExitApplication);
        });
    }

    /// <summary>
    /// Keeps asking the running instance for a while: while a helper is starting up (or while
    /// Windows is busy extracting a single-file build) the pipe simply does not exist yet.
    /// Short connect timeouts make the polling cheap; the generous response timeout covers a
    /// helper whose UI thread is busy.
    /// </summary>
    private async Task<IpcAttempt> SendWithRetriesAsync(IpcRequest request, int deadlineMs = 15000)
    {
        var started = Environment.TickCount64;
        var deadline = started + deadlineMs;
        var announced = false;
        IpcAttempt attempt = new(null, "not tried yet");

        while (Environment.TickCount64 < deadline)
        {
            attempt = await IpcClient.TrySendAsync(request, connectTimeoutMs: 400,
                responseTimeoutMs: Math.Max(3000, request.TimeoutMs)).ConfigureAwait(false);

            if (attempt.Answered)
            {
                _log?.Info($"The running instance answered after {Environment.TickCount64 - started} ms.");
                return attempt;
            }

            _log?.Debug($"Waiting for the running instance: {attempt.Error}");

            if (!announced && Environment.TickCount64 - started > 3000)
            {
                announced = true;
                _log?.Info($"Still waiting for the running instance ({attempt.Error})...");
            }

            await Task.Delay(350).ConfigureAwait(false);
        }

        return attempt;
    }

    /// <summary>Replaces a wedged copy of WinExplorer with this process.</summary>
    private void TakeOver(CommandLineOptions options)
    {
        if (IsExiting)
            return;

        var stopped = WinExplorerProcesses.TerminateOthers(_log);
        if (stopped > 0)
            _log?.Info($"Stopped {stopped} unresponsive WinExplorer process(es).");

        if (!TryAcquireInstance(TimeSpan.FromSeconds(5)))
        {
            _log?.Warn("The single-instance mutex could not be acquired after a restart.");
            MessageBox.Show(
                "WinExplorer could not take over because another copy is still running.\n\n" +
                "Sign out and back in, or end WinExplorer in Task Manager, then start it again.",
                "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExitApplication();
            return;
        }

        _log?.Info("Took over as the primary instance.");
        StartHost(options.Kind == CommandKind.Restart ? options with { Kind = CommandKind.None } : options);
    }

    /// <summary>Creates (and owns) the single-instance mutex, waiting for a dying copy if needed.</summary>
    private bool TryAcquireInstance(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            try
            {
                _instanceMutex?.Dispose();
                _instanceMutex = new Mutex(true, AppPaths.MutexName, out var createdNew);
                _ownsInstance = createdNew;

                if (createdNew)
                    return true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"The single-instance mutex could not be opened: {ex.Message}");
            }

            if (Environment.TickCount64 >= deadline)
                return false;

            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// <c>--restart</c>: ask the running copy to exit, wait for it, and start a fresh helper.
    /// This is the supported way out of "already running but did not answer".
    /// </summary>
    private void RunRestart(CommandLineOptions options)
    {
        _ = Task.Run(async () =>
        {
            _log?.Info("Restart requested.");

            var exit = await IpcClient.TrySendAsync(
                new IpcRequest { Kind = "exit", TimeoutMs = 3000 },
                connectTimeoutMs: 500, responseTimeoutMs: 4000).ConfigureAwait(false);

            if (exit.Answered)
                _log?.Info("The running instance is exiting.");

            if (!TryAcquireInstance(TimeSpan.FromSeconds(6)))
            {
                var stopped = WinExplorerProcesses.TerminateOthers(_log);
                _log?.Info($"Stopped {stopped} WinExplorer process(es) that did not exit.");
                TryAcquireInstance(TimeSpan.FromSeconds(4));
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_ownsInstance)
                {
                    StartHost(options with { Kind = CommandKind.None });
                    return;
                }

                MessageBox.Show(
                    "WinExplorer could not restart because another copy is still running.\n\n" +
                    "End it in Task Manager and start WinExplorer again.",
                    "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                ExitApplication();
            });
        });
    }

    /// <summary>Restart from the tray menu: launch a fresh process that takes over.</summary>
    public void Restart()
    {
        _log?.Info("Restarting WinExplorer.");
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, "--restart")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(AppPaths.ExecutablePath) ?? Environment.CurrentDirectory
            })?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Error("Could not start the replacement process", ex);
            MessageBox.Show(
                $"WinExplorer could not restart.\n\n{ex.Message}",
                "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExitApplication();
    }

    /// <summary><c>--diagnose</c>: writes a report next to the settings and opens it.</summary>
    /// <summary>
    /// Dumps what the app can actually see: every Explorer window, its tabs and the folder
    /// behind each tab, plus the state of the shell automation. Works without the helper, so
    /// it also runs when the helper is wedged.
    /// </summary>
    private void RunProbe()
    {
        string path = Path.Combine(AppPaths.DataDirectory, "WinExplorer-probe.txt");
        try
        {
            AppPaths.EnsureDataDirectory();

            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"WinExplorer probe - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"Version   : {typeof(App).Assembly.GetName().Version}");
            builder.AppendLine($"Windows   : {Environment.OSVersion.VersionString}");
            builder.AppendLine();

            var tabs = new WinExplorer.Shell.TabController(_log!);
            try
            {
                builder.AppendLine("Shell");
                builder.AppendLine("  " + tabs.DescribeShellHealth());
                builder.AppendLine("  shell threads: " + WinExplorer.Services.StaExecutor.HealthSummary());
                builder.AppendLine();

                var windows = WinExplorer.Shell.ExplorerWindows.GetWindows();
                builder.AppendLine($"Windows ({windows.Count})");

                foreach (var window in windows)
                {
                    builder.AppendLine($"  0x{window:X}  '{WinExplorer.Interop.NativeMethods.GetClassName(window)}'");
                    builder.AppendLine($"    MSAA(window) : {WinExplorer.Shell.AddressBarText.TryRead(window) ?? "(nothing)"}");
                    builder.AppendLine($"    UIA(window)  : {WinExplorer.Shell.UiaAddressBar.TryRead(window, maxAgeMs: 0) ?? "(nothing)"}");
                    builder.AppendLine($"    IESW(window) : {WinExplorer.Shell.ShellWindowsQuery.ProbePropertyErrors(window)}");

                    var tabHandles = WinExplorer.Shell.ExplorerWindows.GetTabs(window);
                    if (tabHandles.Count == 0)
                    {
                        builder.AppendLine("    (no tab windows found)");
                        continue;
                    }

                    foreach (var tab in tabHandles)
                    {
                        var location = tabs.GetTabLocationAsync(tab).GetAwaiter().GetResult();
                        builder.AppendLine($"    tab 0x{tab:X}  {location ?? "(no folder reported)"}");

                        // Each engine on its own, so a report says exactly which one is blind.
                        builder.AppendLine($"      MSAA : {WinExplorer.Shell.AddressBarText.TryRead(tab) ?? "(nothing)"}");
                        builder.AppendLine($"      UIA  : {WinExplorer.Shell.UiaAddressBar.TryRead(tab, maxAgeMs: 0) ?? "(nothing)"}");
                        builder.AppendLine($"      IESW : {WinExplorer.Shell.ShellWindowsQuery.ProbePropertyErrors(tab)}");
                    }
                }

                builder.AppendLine();
                builder.AppendLine("Shell automation");
                builder.AppendLine("  " + WinExplorer.Shell.ShellCom.Describe());

                var direct = WinExplorer.Shell.ShellWindowsQuery.Query(out var directError);
                builder.AppendLine($"  IShellWindows direct: {direct.Count} entr(y/ies)" +
                                   (directError.Length == 0 ? string.Empty : $", {directError}"));
                foreach (var (hwnd, entry) in direct)
                    builder.AppendLine($"    0x{hwnd:X}  window 0x{entry.WindowHwnd:X}  '{entry.Location}'");
            }
            finally
            {
                tabs.Dispose();
            }

            File.WriteAllText(path, builder.ToString(), System.Text.Encoding.UTF8);
            ConsoleHelper.WriteLine(builder.ToString());
            ConsoleHelper.WriteLine($"Probe written to {path}");
            Diagnostics.Open(path);
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteLine($"The probe could not be written: {ex.Message}");
        }
    }

    private void RunDiagnostics()
    {
        try
        {
            var path = Diagnostics.Write(_log!, _store!);
            ConsoleHelper.WriteLine($"Diagnostics written to {path}");
            Diagnostics.Open(path);
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteLine($"The diagnostics report could not be written: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // UI
    // ------------------------------------------------------------------

    public void ShowSettingsWindow()
    {
        if (_runtime is null)
            return;

        try
        {
            if (_window is null)
            {
                _viewModel = new MainViewModel(_runtime);
                _window = new MainWindow(this, _viewModel);
                _window.Closed += (_, _) => _window = null;
            }
        }
        catch (Exception ex)
        {
            // A broken window must not take the tray icon and the hooks with it.
            _log?.Error("The settings window could not be created", ex);
            _window = null;
            MessageBox.Show(
                $"The WinExplorer settings window could not be opened.\n\n{ex.Message}\n\n" +
                $"WinExplorer keeps running in the notification area.\nDetails: {AppPaths.LogFile}",
                "WinExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_window is null)
            return;

        try
        {
            if (!_window.IsVisible)
                _window.Show();

            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }
        catch (Exception ex)
        {
            _log?.Error("The settings window could not be shown", ex);
        }
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
