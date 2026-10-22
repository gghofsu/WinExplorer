using System.IO;

namespace WinExplorer.Core;

/// <summary>Where WinExplorer keeps its state. Everything is per-user; no admin needed.</summary>
public static class AppPaths
{
    public const string AppName = "WinExplorer";
    public const string MutexName = @"Local\WinExplorer.SingleInstance.v1";
    public const string PipeName = "WinExplorer.Ipc.v1";

    /// <summary><c>%APPDATA%\WinExplorer</c>.</summary>
    public static string DataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(appData, AppName);
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => Path.Combine(DataDirectory, "WinExplorer.log");

    /// <summary>Full path of the running executable.</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
                return path;

            var main = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrWhiteSpace(main) ? "WinExplorer.exe" : main;
        }
    }

    public static void EnsureDataDirectory()
    {
        try { Directory.CreateDirectory(DataDirectory); }
        catch { /* best effort */ }
    }
}
