using System.Diagnostics;
using System.IO;
using System.Text;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>
/// Writes a report that explains why a copy of WinExplorer does or does not start: whether
/// another instance owns the single-instance mutex, whether it answers on the IPC pipe,
/// which processes exist, which settings are in effect and what the log says.
/// </summary>
internal static class Diagnostics
{
    public static string ReportPath => Path.Combine(AppPaths.DataDirectory, "WinExplorer-diagnose.txt");

    /// <summary>Collects everything and writes it to <see cref="ReportPath"/>.</summary>
    public static string Write(Logger log, SettingsStore store)
    {
        var report = Collect(log, store);

        AppPaths.EnsureDataDirectory();
        File.WriteAllText(ReportPath, report, Encoding.UTF8);
        return ReportPath;
    }

    public static string Collect(Logger log, SettingsStore store)
    {
        var builder = new StringBuilder();

        builder.AppendLine("WinExplorer diagnostics");
        builder.AppendLine($"Generated : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Version   : {typeof(Diagnostics).Assembly.GetName().Version}");
        builder.AppendLine($"Process   : pid {Environment.ProcessId}, "
            + $"{(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, "
            + $"interactive {Environment.UserInteractive}");
        builder.AppendLine($"Executable: {AppPaths.ExecutablePath}");
        builder.AppendLine($"Windows   : {Environment.OSVersion.VersionString}");
        builder.AppendLine();

        builder.AppendLine("Files");
        AppendFile(builder, "Data folder  ", AppPaths.DataDirectory, directory: true);
        AppendFile(builder, "Settings     ", AppPaths.SettingsFile);
        AppendFile(builder, "Log          ", AppPaths.LogFile);
        builder.AppendLine();

        builder.AppendLine("Single instance");
        builder.AppendLine($"  mutex   : {DescribeMutex()}");
        builder.AppendLine($"  pipe    : {DescribePipe()}");
        builder.AppendLine();

        builder.AppendLine("WinExplorer processes");
        foreach (var line in WinExplorerProcesses.Describe())
            builder.AppendLine(line);
        builder.AppendLine();

        builder.AppendLine("Settings");
        var settings = store.Current;
        builder.AppendLine($"  Enabled                 : {settings.Enabled}");
        builder.AppendLine($"  Start with Windows      : {settings.StartWithWindows} "
            + $"(registry says {(SafeStartupEnabled(log) is { } on ? on.ToString() : "unknown")})");
        builder.AppendLine($"  Folder context menu     : {settings.ContextMenuInstalled}");
        builder.AppendLine($"  Double-click as tab     : {settings.InterceptDoubleClick}");
        builder.AppendLine($"  Middle-click as tab     : {settings.InterceptMiddleClick}");
        builder.AppendLine($"  Fold new windows        : {settings.MergeNewWindows}");
        builder.AppendLine($"  Minimize to tray        : {settings.MinimizeToTrayOnClose}");
        builder.AppendLine();

        builder.AppendLine("Recent log");
        var tail = log.Tail(80);
        if (tail.Count == 0)
        {
            builder.AppendLine("  (the log is empty)");
        }
        else
        {
            foreach (var line in tail)
                builder.AppendLine("  " + line);
        }

        return builder.ToString();
    }

    /// <summary>Opens the report in the default text editor.</summary>
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteLine($"The report was written to {path} ({ex.Message}).");
        }
    }

    private static bool? SafeStartupEnabled(Logger log)
    {
        try
        {
            return new StartupRegistration(log).IsEnabled();
        }
        catch
        {
            return null;
        }
    }

    private static void AppendFile(StringBuilder builder, string label, string path, bool directory = false)
    {
        try
        {
            if (!directory && File.Exists(path))
            {
                builder.AppendLine($"  {label}: {path} ({new FileInfo(path).Length:n0} bytes)");
            }
            else if (directory && Directory.Exists(path))
            {
                builder.AppendLine($"  {label}: {path} (exists)");
            }
            else
            {
                builder.AppendLine($"  {label}: {path} (missing)");
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"  {label}: {path} ({ex.GetType().Name})");
        }
    }

    private static string DescribeMutex()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(AppPaths.MutexName);
            if (mutex.WaitOne(0))
            {
                mutex.ReleaseMutex();
                return $"{AppPaths.MutexName} exists but nobody owns it";
            }

            return $"{AppPaths.MutexName} is owned by a running instance";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return $"{AppPaths.MutexName} does not exist (no instance is running)";
        }
        catch (Exception ex)
        {
            return $"{AppPaths.MutexName} ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string DescribePipe()
    {
        try
        {
            var started = Environment.TickCount64;
            var attempt = IpcClient
                .TrySendAsync(new IpcRequest { Kind = "ping", TimeoutMs = 2000 }, 600, 2500)
                .GetAwaiter()
                .GetResult();
            var elapsed = Environment.TickCount64 - started;

            if (attempt.Answered)
                return $"answered in {elapsed} ms: {attempt.Response!.Outcome} {attempt.Response.Message}".TrimEnd();

            return $"no answer ({attempt.Error})";
        }
        catch (Exception ex)
        {
            return $"no answer ({ex.GetType().Name}: {ex.Message})";
        }
    }
}
