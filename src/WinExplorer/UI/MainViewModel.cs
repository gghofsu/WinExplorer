using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WinExplorer.Core;
using WinExplorer.Services;

namespace WinExplorer.UI;

/// <summary>
/// View model for the settings window. Every change is written straight back to
/// <c>settings.json</c> (and applied to the running hooks), so there is no Save button.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppRuntime _runtime;
    private bool _suppressSave;
    private string _statusText = "Ready.";
    private string? _selectedExcludedPath;

    internal MainViewModel(AppRuntime runtime)
    {
        _runtime = runtime;

        Activity = new ObservableCollection<string>();
        ExcludedPaths = new ObservableCollection<string>(runtime.Settings.ExcludedPaths);

        AlreadyOpenOptions = new[]
        {
            new EnumOption<AlreadyOpenBehavior>(AlreadyOpenBehavior.SwitchToExistingTab,
                "Switch to the tab that already shows it", "Default: no duplicates."),
            new EnumOption<AlreadyOpenBehavior>(AlreadyOpenBehavior.OpenAnotherTab,
                "Open another tab anyway", "Duplicates allowed.")
        };

        TargetWindowOptions = new[]
        {
            new EnumOption<TargetWindowPolicy>(TargetWindowPolicy.ForegroundWindow,
                "The window I am using"),
            new EnumOption<TargetWindowPolicy>(TargetWindowPolicy.LastUsedWindow,
                "The last window I used"),
            new EnumOption<TargetWindowPolicy>(TargetWindowPolicy.LargestWindow,
                "The window with the most tabs")
        };

        TogglePauseCommand = new RelayCommand(() =>
        {
            _runtime.ToggleEnabled();
            RefreshFromSettings();
        });

        ResetHotkeysCommand = new RelayCommand(() =>
        {
            DuplicateHotkey = HotkeySpec.DuplicateTabDefault.ToString();
            ReopenClosedHotkey = HotkeySpec.ReopenClosedDefault.ToString();
            TogglePauseHotkey = HotkeySpec.TogglePauseDefault.ToString();
        });

        AddExcludedPathCommand = new RelayCommand(AddExcludedPath);
        RemoveExcludedPathCommand = new RelayCommand(() =>
        {
            if (SelectedExcludedPath is not null)
                ExcludedPaths.Remove(SelectedExcludedPath);
            SaveExcludedPaths();
        }, () => SelectedExcludedPath is not null);

        OpenLogCommand = new RelayCommand(() => OpenInShell(AppPaths.LogFile));
        OpenDataFolderCommand = new RelayCommand(() => OpenInShell(AppPaths.DataDirectory));
        ClearActivityCommand = new RelayCommand(() => Activity.Clear());

        runtime.Activity += (_, message) => Post(() => AddActivity(message));
        runtime.Status += (_, message) => Post(() => StatusText = message);
        runtime.SettingsChanged += (_, _) => Post(RefreshFromSettings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Collections ----------------------------------------------------
    public ObservableCollection<string> Activity { get; }
    public ObservableCollection<string> ExcludedPaths { get; }
    public IReadOnlyList<EnumOption<AlreadyOpenBehavior>> AlreadyOpenOptions { get; }
    public IReadOnlyList<EnumOption<TargetWindowPolicy>> TargetWindowOptions { get; }

    public string? SelectedExcludedPath
    {
        get => _selectedExcludedPath;
        set
        {
            if (Set(ref _selectedExcludedPath, value))
                ((RelayCommand)RemoveExcludedPathCommand).RaiseCanExecuteChanged();
        }
    }

    // ---- Commands -------------------------------------------------------
    public ICommand TogglePauseCommand { get; }
    public ICommand ResetHotkeysCommand { get; }
    public ICommand AddExcludedPathCommand { get; }
    public ICommand RemoveExcludedPathCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ClearActivityCommand { get; }

    // ---- Status ---------------------------------------------------------
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsEnabled => _runtime.Settings.Enabled;
    public string ToggleLabel => IsEnabled ? "Pause" : "Resume";
    public string Headline => IsEnabled ? "Active" : "Paused";

    public string VersionText
    {
        get
        {
            var version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string DataFolder => AppPaths.DataDirectory;

    // ---- Behaviour ------------------------------------------------------
    public bool InterceptDoubleClick
    {
        get => _runtime.Settings.InterceptDoubleClick;
        set => SetSetting(s => s.InterceptDoubleClick = value);
    }

    public bool InterceptMiddleClick
    {
        get => _runtime.Settings.InterceptMiddleClick;
        set => SetSetting(s => s.InterceptMiddleClick = value);
    }

    public bool ActivateNewTab
    {
        get => _runtime.Settings.ActivateNewTab;
        set => SetSetting(s => s.ActivateNewTab = value);
    }

    public bool DedupeAcrossAllWindows
    {
        get => _runtime.Settings.DedupeAcrossAllWindows;
        set => SetSetting(s => s.DedupeAcrossAllWindows = value);
    }

    public AlreadyOpenBehavior WhenAlreadyOpen
    {
        get => _runtime.Settings.WhenAlreadyOpen;
        set => SetSetting(s => s.WhenAlreadyOpen = value);
    }

    public TargetWindowPolicy TargetWindow
    {
        get => _runtime.Settings.TargetWindow;
        set => SetSetting(s => s.TargetWindow = value);
    }

    // ---- Windows --------------------------------------------------------
    public bool MergeNewWindows
    {
        get => _runtime.Settings.MergeNewWindows;
        set => SetSetting(s => s.MergeNewWindows = value);
    }

    public bool KeepVirtualFoldersAsWindows
    {
        get => _runtime.Settings.KeepVirtualFoldersAsWindows;
        set => SetSetting(s => s.KeepVirtualFoldersAsWindows = value);
    }

    public bool TakeOverWinE
    {
        get => _runtime.Settings.TakeOverWinE;
        set => SetSetting(s => s.TakeOverWinE = value);
    }

    // ---- Integration ----------------------------------------------------
    public bool ContextMenuInstalled
    {
        get => _runtime.Settings.ContextMenuInstalled;
        set
        {
            if (value == _runtime.Settings.ContextMenuInstalled)
                return;

            if (_suppressSave)
                return;

            var ok = value ? _runtime.Shell.Install() : _runtime.Shell.Uninstall();
            _runtime.Settings.ContextMenuInstalled = value;
            if (!ok)
                StatusText = "Windows would not let me change the context menu.";

            Save();
            Raise();
        }
    }

    public bool DefaultFolderHandler
    {
        get => _runtime.Shell.IsDefaultFolderHandler();
        set
        {
            if (_suppressSave)
                return;

            var ok = _runtime.Shell.SetAsDefaultFolderHandler(value);
            if (!ok)
                StatusText = "Windows would not let me change the default folder handler.";
            Raise();
        }
    }

    public bool StartWithWindows
    {
        get => _runtime.Startup.IsEnabled();
        set
        {
            if (_suppressSave)
                return;

            _runtime.Startup.SetEnabled(value);
            _runtime.Settings.StartWithWindows = value;
            Save();
            Raise();
        }
    }

    public bool StartMinimized
    {
        get => _runtime.Settings.StartMinimized;
        set => SetSetting(s => s.StartMinimized = value);
    }

    public bool MinimizeToTrayOnClose
    {
        get => _runtime.Settings.MinimizeToTrayOnClose;
        set => SetSetting(s => s.MinimizeToTrayOnClose = value);
    }

    public bool ShowNotifications
    {
        get => _runtime.Settings.ShowNotifications;
        set => SetSetting(s => s.ShowNotifications = value);
    }

    // ---- Shortcuts ------------------------------------------------------
    public string DuplicateHotkey
    {
        get => _runtime.Settings.DuplicateTabHotkey;
        set => SetHotkey(value, s => s.DuplicateTabHotkey = value);
    }

    public string ReopenClosedHotkey
    {
        get => _runtime.Settings.ReopenClosedHotkey;
        set => SetHotkey(value, s => s.ReopenClosedHotkey = value);
    }

    public string TogglePauseHotkey
    {
        get => _runtime.Settings.TogglePauseHotkey;
        set => SetHotkey(value, s => s.TogglePauseHotkey = value);
    }

    // ---- Helpers --------------------------------------------------------

    private void SetHotkey(string text, Action<AppSettings> apply)
    {
        if (!HotkeySpec.TryParse(text, out _, out var error))
        {
            StatusText = error ?? "That shortcut is not valid.";
            Raise();
            return;
        }

        if (!SetSetting(apply))
            return;

        StatusText = "Shortcut saved.";
    }

    private bool SetSetting(Action<AppSettings> apply)
    {
        if (_suppressSave)
            return false;

        apply(_runtime.Settings);
        Save();
        Raise();
        return true;
    }

    private void Save()
    {
        _runtime.SaveSettings(_runtime.Settings);
    }

    private void SaveExcludedPaths()
    {
        _runtime.Settings.ExcludedPaths = ExcludedPaths.ToList();
        Save();
    }

    private void AddExcludedPath()
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder that should keep the normal Windows behaviour",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            var normalized = PathNormalizer.Normalize(dialog.SelectedPath);
            if (normalized.Length == 0 || ExcludedPaths.Contains(normalized))
                return;

            ExcludedPaths.Add(normalized);
            SaveExcludedPaths();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not add the folder: {ex.Message}";
        }
    }

    public void AddExcludedPath(string path)
    {
        var normalized = PathNormalizer.Normalize(path);
        if (normalized.Length == 0 || ExcludedPaths.Contains(normalized))
            return;

        ExcludedPaths.Add(normalized);
        SaveExcludedPaths();
    }

    public void AddActivity(string message)
    {
        Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Activity.Count > 200)
            Activity.RemoveAt(Activity.Count - 1);
    }

    public void RefreshFromSettings()
    {
        _suppressSave = true;
        try
        {
            if (!ExcludedPaths.SequenceEqual(_runtime.Settings.ExcludedPaths))
            {
                ExcludedPaths.Clear();
                foreach (var path in _runtime.Settings.ExcludedPaths)
                    ExcludedPaths.Add(path);
            }
        }
        finally
        {
            _suppressSave = false;
        }

        Raise();
    }

    private static void Post(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Notifies the UI that every binding should be refreshed.</summary>
    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
