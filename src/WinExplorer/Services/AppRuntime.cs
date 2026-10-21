using System.IO;
using WinExplorer.Core;
using WinExplorer.Hooks;
using WinExplorer.Interop;
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
    private readonly TabController _tabs;
    private readonly ExplorerService _service;
    private readonly ExplorerWatcher _watcher;
    private readonly MouseHook _mouseHook;
    private readonly KeyboardHook _keyboardHook;
    private readonly ShellIntegration _shell;
    private readonly StartupRegistration _startup;
    private bool _disposed;
    private bool _started;

    public AppRuntime(Logger log, SettingsStore settings)
    {
        _log = log;
        _settings = settings;
        _tabs = new TabController(log);
        _service = new ExplorerService(_tabs, settings, log);
        _watcher = new ExplorerWatcher(log, _service, _tabs);
        _mouseHook = new MouseHook(log, () => _settings.Current, HandleFolderClickAsync);
        _keyboardHook = new KeyboardHook(log, () => _settings.Current, OnHotkey);
        _shell = new ShellIntegration(log);
        _startup = new StartupRegistration(log);

        _service.Opened += (_, result) =>
            Activity?.Invoke(this, ExplorerService.Describe(result));
        _tabs.Activity += (_, message) => Activity?.Invoke(this, message);
        _watcher.Merged += (_, message) => Activity?.Invoke(this, message);
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

    public bool IsEnabled => _settings.Current.Enabled;

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        ApplyHooks();
        _watcher.Start();
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

        if (settings.Enabled && (settings.InterceptDoubleClick || settings.InterceptMiddleClick))
            _mouseHook.Start();
        else
            _mouseHook.Stop();

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

    /// <summary>
    /// Turns "the user double-clicked a folder row" into an open request, or replays the
    /// click when the row was not a folder after all.
    /// </summary>
    private async Task HandleFolderClickAsync(FolderClick click)
    {
        var settings = _settings.Current;

        if (!settings.Enabled)
        {
            Replay(click);
            return;
        }

        var location = await _tabs.GetTabLocationAsync(click.Tab).ConfigureAwait(false);
        if (string.IsNullOrEmpty(location))
            location = await _tabs.GetActiveLocationAsync(click.Window).ConfigureAwait(false);

        var folder = ResolveFolder(location, click.ItemName);
        if (folder is null || settings.IsExcluded(folder))
        {
            Replay(click);
            return;
        }

        // Both click kinds reuse an open tab: a middle-click must not duplicate a folder
        // either (it only differs in that the resulting tab stays in the background).
        const OpenMode mode = OpenMode.ReuseExistingTab;

        var origin = click.Kind == FolderClickKind.MiddleClick ? OpenOrigin.MiddleClick : OpenOrigin.DoubleClick;
        var request = new OpenRequest
        {
            Path = folder,
            Mode = mode,
            Origin = origin,
            PreferredWindow = click.Window,
            // A middle-click mimics Windows: the tab opens in the background.
            Activate = click.Kind != FolderClickKind.MiddleClick
        };

        _log.Debug($"{origin} -> '{folder}' (mode: {mode}).");

        OpenResult result;
        try
        {
            result = await _service.OpenAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Folder click failed", ex);
            Replay(click);
            return;
        }

        if (!result.Succeeded)
            Replay(click);
    }

    /// <summary>Combines the current folder with the display name of the clicked row.</summary>
    internal static string? ResolveFolder(string? currentLocation, string? itemName)
    {
        if (string.IsNullOrWhiteSpace(currentLocation) || string.IsNullOrWhiteSpace(itemName))
            return null;

        if (PathNormalizer.IsShellNamespace(currentLocation))
            return null;

        // Display names never contain separators; reject anything that tries to escape the
        // current folder.
        if (itemName.IndexOfAny(new[] { '\\', '/' }) >= 0 ||
            itemName is "." or "..")
        {
            return null;
        }

        try
        {
            // Display names can differ in punctuation/case; match against the real entries
            // first so the result keeps the casing that is stored on disk.
            var match = Directory.EnumerateDirectories(currentLocation)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), itemName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return PathNormalizer.Normalize(match);

            var candidate = Path.Combine(currentLocation, itemName);
            return Directory.Exists(candidate) ? PathNormalizer.Normalize(candidate) : null;
        }
        catch
        {
            return null;
        }
    }

    private void Replay(FolderClick click)
    {
        _log.Debug($"Replaying {click.Kind} at ({click.X},{click.Y}).");
        if (click.Kind == FolderClickKind.MiddleClick)
            MouseHook.ReplayMiddleClick(click.X, click.Y);
        else
            MouseHook.ReplayDoubleClick(click.X, click.Y);
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
                    var folded = await _service.ConsolidateWindowsAsync(window).ConfigureAwait(false);
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

        try { _watcher.Stop(); } catch { /* ignore */ }
        try { _mouseHook.Dispose(); } catch { /* ignore */ }
        try { _keyboardHook.Dispose(); } catch { /* ignore */ }
        try { _tabs.Dispose(); } catch { /* ignore */ }
        try { _service.Dispose(); } catch { /* ignore */ }
        _log.Info("WinExplorer stopped.");
    }
}
