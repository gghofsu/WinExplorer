using Microsoft.Win32;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>Registers the folder context menu entries and the optional default-handler.</summary>
/// <remarks>
/// Everything is written under <c>HKCU\Software\Classes</c>, i.e. per user and without
/// elevation. The previous value of the <c>Directory\shell</c> default verb is backed up so
/// uninstalling restores double-click behaviour exactly as it was.
/// </remarks>
public sealed class ShellIntegration
{
    private const string VerbOpenTab = "WinExplorer.OpenTab";
    private const string VerbOpenWindow = "WinExplorer.OpenWindow";
    private const string VerbOpenTabHere = "WinExplorer.OpenTabHere";
    private const string BackupKey = @"Software\WinExplorer\Backup";
    private const string ClassesRoot = @"Software\Classes";

    private readonly Logger _log;
    private readonly Func<string> _executablePath;

    public ShellIntegration(Logger log, Func<string>? executablePath = null)
    {
        _log = log;
        _executablePath = executablePath ?? (() => AppPaths.ExecutablePath);
    }

    public bool IsInstalled => IsVerbPresent(@"Directory\shell\" + VerbOpenTab);

    /// <summary>Adds (or refreshes) the right-click entries. Returns true on success.</summary>
    public bool Install()
    {
        try
        {
            var exe = _executablePath();

            CreateVerb($@"Directory\shell\{VerbOpenTab}", "Open in WinExplorer tab", exe,
                $"\"{exe}\" --open \"%1\"");
            CreateVerb($@"Directory\shell\{VerbOpenWindow}", "Open in a new window (WinExplorer)", exe,
                $"\"{exe}\" --open \"%1\" --new-window");
            CreateVerb($@"Directory\Background\shell\{VerbOpenTabHere}", "Open this folder in a WinExplorer tab", exe,
                $"\"{exe}\" --open \"%V\"");
            CreateVerb($@"Drive\shell\{VerbOpenTab}", "Open in WinExplorer tab", exe,
                $"\"{exe}\" --open \"%1\"");

            _log.Info("Folder context menu entries installed.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Installing the context menu failed", ex);
            return false;
        }
    }

    /// <summary>Makes WinExplorer the default (double-click) handler for file system folders.</summary>
    public bool SetAsDefaultFolderHandler(bool enable)
    {
        try
        {
            using var shellKey = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\Directory\shell", true);
            if (shellKey is null)
                return false;

            if (enable)
            {
                BackupDefaultVerb(shellKey.GetValue(null) as string);
                shellKey.SetValue(null, VerbOpenTab);
                _log.Info("WinExplorer is now the default folder handler (HKCU).");
            }
            else
            {
                RestoreDefaultVerb(shellKey);
                _log.Info("Default folder handler restored.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Changing the default folder handler failed", ex);
            return false;
        }
    }

    private void BackupDefaultVerb(string? current)
    {
        using var backup = Registry.CurrentUser.CreateSubKey(BackupKey, true);
        if (backup is null)
            return;

        // Only remember the *original* value, never overwrite a backup with our own verb.
        if (backup.GetValue("DirectoryShellDefault") is not null)
            return;

        backup.SetValue("DirectoryShellDefault", current ?? string.Empty);
        backup.SetValue("DirectoryShellDefaultExisted", current is null ? 0 : 1, RegistryValueKind.DWord);
    }

    private void RestoreDefaultVerb(RegistryKey shellKey)
    {
        using var backup = Registry.CurrentUser.OpenSubKey(BackupKey, true);
        var stored = backup?.GetValue("DirectoryShellDefault") as string;
        var existed = Convert.ToInt32(backup?.GetValue("DirectoryShellDefaultExisted") ?? 0) == 1;

        if (existed && stored is not null)
            shellKey.SetValue(null, stored);
        else
            shellKey.DeleteValue(string.Empty, throwOnMissingValue: false);

        backup?.DeleteValue("DirectoryShellDefault", throwOnMissingValue: false);
        backup?.DeleteValue("DirectoryShellDefaultExisted", throwOnMissingValue: false);
    }

    /// <summary>Removes every context menu entry and restores the default handler.</summary>
    public bool Uninstall()
    {
        try
        {
            using var shellRoot = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\Directory\shell", true);

            // Only restore the double-click handler while it is still ours: the user may have
            // replaced it with something else after installing WinExplorer.
            if (shellRoot is not null &&
                string.Equals(shellRoot.GetValue(null) as string, VerbOpenTab, StringComparison.OrdinalIgnoreCase))
            {
                RestoreDefaultVerb(shellRoot);
            }

            DeleteKey($@"Directory\shell\{VerbOpenTab}");
            DeleteKey($@"Directory\shell\{VerbOpenWindow}");
            DeleteKey($@"Directory\Background\shell\{VerbOpenTabHere}");
            DeleteKey($@"Drive\shell\{VerbOpenTab}");

            _log.Info("Folder context menu entries removed.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Removing the context menu failed", ex);
            return false;
        }
    }

    private void CreateVerb(string relativeKey, string label, string iconPath, string command)
    {
        using var verb = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{relativeKey}", true);
        if (verb is null)
            return;

        verb.SetValue(null, label);
        verb.SetValue("Icon", $"\"{iconPath}\",0");

        using var commandKey = verb.CreateSubKey("command", true);
        commandKey?.SetValue(null, command);
    }

    private static void DeleteKey(string relativeKey)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{relativeKey}", throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsVerbPresent(string relativeKey) =>
        Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{relativeKey}") is not null;

    /// <summary>True when WinExplorer currently owns the double-click action for folders.</summary>
    public bool IsDefaultFolderHandler()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\Directory\shell");
        return string.Equals(key?.GetValue(null) as string, VerbOpenTab, StringComparison.OrdinalIgnoreCase);
    }
}
