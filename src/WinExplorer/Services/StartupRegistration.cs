using Microsoft.Win32;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>Registers WinExplorer in the per-user Run key so the helper is always running.</summary>
public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = AppPaths.AppName;

    private readonly Logger _log;
    private readonly Func<string> _executablePath;

    public StartupRegistration(Logger log, Func<string>? executablePath = null)
    {
        _log = log;
        _executablePath = executablePath ?? (() => AppPaths.ExecutablePath);
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] data && data.Length > 0 && data[0] % 2 == 1)
                return false; // disabled from Task Manager > Startup

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, true);
                run?.SetValue(ValueName, $"\"{_executablePath()}\" --background");

                using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey, true);
                var data = new byte[12];
                data[0] = 0x02; // even = enabled
                approved?.SetValue(ValueName, data, RegistryValueKind.Binary);
                _log.Info("Added to startup.");
            }
            else
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
                run?.DeleteValue(ValueName, throwOnMissingValue: false);

                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Info("Removed from startup.");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Updating startup registration failed", ex);
            return false;
        }
    }
}
