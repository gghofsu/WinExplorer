using WinExplorer.Core;
using WinExplorer.Shell;

namespace WinExplorer.Services;

/// <summary>
/// The thin policy layer between the app (IPC, tray, hotkeys, context menu) and the
/// <see cref="ExplorerWatcher"/> engine: it turns an <see cref="OpenRequest"/> into the
/// right watcher call and reports what happened.
/// </summary>
internal sealed class ExplorerService : IDisposable
{
    private readonly ExplorerWatcher _watcher;
    private readonly SettingsStore _settings;
    private readonly Logger _log;
    private readonly TabHistory _history;

    public ExplorerService(ExplorerWatcher watcher, SettingsStore settings, Logger log, TabHistory history)
    {
        _watcher = watcher;
        _settings = settings;
        _log = log;
        _history = history;
    }

    /// <summary>Raised after every open attempt, for logging/notifications.</summary>
    public event EventHandler<OpenResult>? Opened;

    /// <summary>Raised when something noteworthy happened that the UI should show.</summary>
    public event EventHandler<string>? Status;

    public AppSettings Settings => _settings.Current;

    public TabHistory History => _history;

    // ------------------------------------------------------------------

    public async Task<OpenResult> OpenAsync(OpenRequest request, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        var path = PathNormalizer.Normalize(request.Path);

        if (path.Length == 0 && request.Mode != OpenMode.NewWindow)
            path = ShellDefaults.GetLaunchLocation();

        try
        {
            if (!settings.Enabled)
            {
                // Paused: behave like plain Windows would (still honour an explicit request
                // so the context menu keeps working while the helper is paused).
                return Report(_watcher.OpenNewWindow(path), request);
            }

            var result = request.Mode switch
            {
                OpenMode.NewWindow => _watcher.OpenNewWindow(path),

                OpenMode.NewTabInExistingWindow => await _watcher.OpenAsTabAsync(path,
                    activate: request.Activate, allowDuplicate: true,
                    preferredWindow: request.PreferredWindow).ConfigureAwait(false),

                _ => await _watcher.OpenAsTabAsync(path,
                    activate: request.Activate, allowDuplicate: false,
                    preferredWindow: request.PreferredWindow).ConfigureAwait(false)
            };

            return Report(result, request);
        }
        catch (OperationCanceledException)
        {
            return new OpenResult(OpenOutcome.Failed, path, Message: "Cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error($"Opening '{path}' failed", ex);
            return Report(new OpenResult(OpenOutcome.Failed, path, Message: ex.Message), request);
        }
    }

    public async Task<OpenResult> DuplicateActiveTabAsync(nint window = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await _watcher.DuplicateActiveTabAsync(window).ConfigureAwait(false);
        Opened?.Invoke(this, result);
        if (_settings.Current.ShowNotifications && result.Succeeded)
            Status?.Invoke(this, Describe(result));
        return result;
    }

    public async Task<OpenResult> ReopenLastClosedAsync(nint window = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await _watcher.ReopenLastClosedAsync(window).ConfigureAwait(false);
        Opened?.Invoke(this, result);
        if (_settings.Current.ShowNotifications && result.Succeeded)
            Status?.Invoke(this, Describe(result));
        return result;
    }

    /// <summary>Folds every other Explorer window into one tabbed window.</summary>
    public Task<int> ConsolidateWindowsAsync(nint keepWindow = 0, bool force = false,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.Enabled && !force)
            return Task.FromResult(0);

        return _watcher.ConsolidateAsync(keepWindow);
    }

    // ------------------------------------------------------------------

    private OpenResult Report(OpenResult result, OpenRequest request)
    {
        Opened?.Invoke(this, result);
        if (_settings.Current.ShowNotifications && result.Succeeded)
            Status?.Invoke(this, Describe(result));
        return result;
    }

    public static string Describe(OpenResult result) => result.Outcome switch
    {
        OpenOutcome.SwitchedToExistingTab =>
            $"Switched to \"{PathNormalizer.ToDisplayName(result.Path)}\".",
        OpenOutcome.OpenedNewTab =>
            $"Opened \"{PathNormalizer.ToDisplayName(result.Path)}\" in a new tab.",
        OpenOutcome.OpenedNewWindow =>
            $"Opened \"{PathNormalizer.ToDisplayName(result.Path)}\" in a new window.",
        _ => result.Message ?? "Could not open the folder."
    };

    public void Dispose()
    {
    }
}
