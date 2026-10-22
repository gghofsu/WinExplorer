using WinExplorer.Core;
using WinExplorer.Hooks;
using WinExplorer.Shell;

namespace WinExplorer.Services;

/// <summary>
/// Owns every background component and translates user actions into
/// <see cref="ExplorerService"/> calls. The UI, the tray icon and the IPC server all talk
/// to this one object.
/// </summary>
internal sealed class AppRuntime : IDisposable
{
    private readonly Logger _log;
    private readonly SettingsStore _settings;
    private readonly TabHistory _history;
    private readonly ExplorerWatcher _watcher;
    private readonly ExplorerService _service;
    private readonly KeyboardHook _keyboardHook;
    private readonly ShellIntegration _shell;
    private readonly StartupRegistration _startup;
    private bool _disposed;
    private bool _started;

    public AppRuntime(Logger log, SettingsStore settings)
    {
        _log = log;
        _settings = settings;
        _history = new TabHistory();
        _watcher = new ExplorerWatcher(log, settings, _history);
        _service = new ExplorerService(_watcher, settings, log, _history);
        _keyboardHook = new KeyboardHook(log, () => _settings.Current, OnHotkey);
        _shell = new ShellIntegration(log);
        _startup = new StartupRegistration(log);

        _service.Opened += (_, result) =>
            Activity?.Invoke(this, ExplorerService.Describe(result));
        _watcher.Activity += (_, message) => Activity?.Invoke(this, message);
        _service.Status += (_, message) => Status?.Invoke(this, message);
    }

    /// <summary>Human readable one-liners for the activity list and tray balloons.</summary>
    public event EventHandler<string>? Activity;

    /// <summary>Short status messages ("Paused", "Resumed", ...).</summary>
    public event EventHandler<string>? Status;

    /// <summary>Raised after settings were persisted (from the UI, a hotkey or the IPC).</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Settings => _settings.Current;

    public Logger Logger => _log;

    public TabHistory History => _service.History;

    public ShellIntegration Shell => _shell;

    public StartupRegistration Startup => _startup;

    internal ExplorerWatcher Watcher => _watcher;

    public bool IsEnabled => _settings.Current.Enabled;

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        ApplyHooks();
        _ = _watcher.StartAsync();
        _log.Info($"WinExplorer started (enabled: {_settings.Current.Enabled}).");
        Status?.Invoke(this, _settings.Current.Enabled ? "WinExplorer is active." : "WinExplorer is paused.");
    }

    /// <summary>
    /// Re-reads the settings and starts/stops the hooks accordingly. Low level hooks only
    /// deliver callbacks on the thread that installed them, so this always runs on the UI
    /// thread - whoever calls it (a hotkey, the IPC thread or the settings window).
    /// </summary>
    public void ApplyHooks()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyHooksCore));
            return;
        }

        ApplyHooksCore();
    }

    private void ApplyHooksCore()
    {
        var settings = _settings.Current;

        if (settings.Enabled && (HasHotkeys(settings) || settings.TakeOverWinE))
            _keyboardHook.Start();
        else
            _keyboardHook.Stop();
    }

    private static bool HasHotkeys(AppSettings settings)
    {
        var (duplicate, reopen, toggle) = settings.GetHotkeys();
        return !duplicate.IsEmpty || !reopen.IsEmpty || !toggle.IsEmpty;
    }

    public void SaveSettings(AppSettings settings)
    {
        _settings.Save(settings);
        ApplyHooks();
        SettingsChanged?.Invoke(this, settings);
        Status?.Invoke(this, settings.Enabled ? "Settings saved. WinExplorer is active." : "Settings saved. WinExplorer is paused.");
    }

    public void SetEnabled(bool enabled)
    {
        if (_settings.Current.Enabled == enabled)
            return;

        // Mutate in place: the settings window binds to this very instance.
        _settings.Current.Enabled = enabled;
        SaveSettings(_settings.Current);
    }

    public bool ToggleEnabled()
    {
        SetEnabled(!_settings.Current.Enabled);
        return _settings.Current.Enabled;
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    public Task<OpenResult> OpenAsync(OpenRequest request, CancellationToken cancellationToken = default) =>
        _service.OpenAsync(request, cancellationToken);

    /// <summary>Folds every other Explorer window into one tabbed window.</summary>
    public Task<int> ConsolidateWindowsAsync(nint keepWindow = 0) =>
        _service.ConsolidateWindowsAsync(keepWindow, force: true);

    public Task<OpenResult> DuplicateActiveTabAsync(nint window = 0, CancellationToken cancellationToken = default) =>
        _service.DuplicateActiveTabAsync(window, cancellationToken);

    public Task<OpenResult> ReopenLastClosedAsync(nint window = 0, CancellationToken cancellationToken = default) =>
        _service.ReopenLastClosedAsync(window, cancellationToken);

    private void OnHotkey(HotkeyAction action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (action)
                {
                    case HotkeyAction.TogglePause:
                    {
                        var enabled = ToggleEnabled();
                        Activity?.Invoke(this, enabled ? "Resumed." : "Paused.");
                        break;
                    }
                    case HotkeyAction.DuplicateTab:
                        await DuplicateActiveTabAsync(ExplorerWindows.GetForegroundWindow()).ConfigureAwait(false);
                        break;
                    case HotkeyAction.ReopenClosedTab:
                        await ReopenLastClosedAsync(ExplorerWindows.GetForegroundWindow()).ConfigureAwait(false);
                        break;
                    case HotkeyAction.NewTabHere:
                        await OpenAsync(OpenRequest.Reuse(ShellDefaults.GetLaunchLocation(), OpenOrigin.WinE,
                            ExplorerWindows.GetForegroundWindow())).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Hotkey {action} failed", ex);
            }
        });
    }

    // ------------------------------------------------------------------
    // IPC
    // ------------------------------------------------------------------

    public async Task<IpcResponse> HandleIpcAsync(IpcRequest request)
    {
        try
        {
            var window = (nint)request.Window;

            switch (request.Kind)
            {
                case "open":
                {
                    if (string.IsNullOrWhiteSpace(request.Path))
                        return new IpcResponse { Ok = false, Message = "No folder was given." };

                    var mode = request.Mode switch
                    {
                        "newtab" => OpenMode.NewTabInExistingWindow,
                        "newwindow" => OpenMode.NewWindow,
                        _ => OpenMode.ReuseExistingTab
                    };

                    var result = await _service.OpenAsync(new OpenRequest
                    {
                        Path = request.Path,
                        Mode = mode,
                        Origin = OpenOrigin.CommandLine,
                        PreferredWindow = window,
                        Activate = request.Activate
                    }).ConfigureAwait(false);

                    return ToResponse(result);
                }

                case "duplicate":
                    return ToResponse(await _service.DuplicateActiveTabAsync(window).ConfigureAwait(false));

                case "reopen":
                    return ToResponse(await _service.ReopenLastClosedAsync(window).ConfigureAwait(false));

                case "toggle":
                {
                    var enabled = ToggleEnabled();
                    return new IpcResponse { Ok = true, Outcome = enabled ? "resumed" : "paused" };
                }

                case "pause":
                    SetEnabled(false);
                    return new IpcResponse { Ok = true, Outcome = "paused" };

                case "resume":
                    SetEnabled(true);
                    return new IpcResponse { Ok = true, Outcome = "resumed" };

                case "ping":
                    return new IpcResponse { Ok = true, Outcome = "pong" };

                case "consolidate":
                {
                    var folded = await _service.ConsolidateWindowsAsync(window, force: true).ConfigureAwait(false);
                    return new IpcResponse
                    {
                        Ok = true,
                        Outcome = folded > 0 ? $"folded {folded}" : "single window"
                    };
                }

                default:
                    return new IpcResponse { Ok = false, Message = $"Unknown request '{request.Kind}'." };
            }
        }
        catch (Exception ex)
        {
            _log.Error("Handling an IPC request failed", ex);
            return new IpcResponse { Ok = false, Message = ex.Message };
        }
    }

    private static IpcResponse ToResponse(OpenResult result) => new()
    {
        Ok = result.Succeeded,
        Outcome = result.Outcome.ToString(),
        Message = result.Message,
        Window = result.Window,
        Tab = result.Tab
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _keyboardHook.Dispose(); } catch { /* ignore */ }
        try { _watcher.Dispose(); } catch { /* ignore */ }
        try { _service.Dispose(); } catch { /* ignore */ }
        _log.Info("WinExplorer stopped.");
    }
}
