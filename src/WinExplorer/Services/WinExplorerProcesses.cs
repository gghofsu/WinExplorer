using System.Diagnostics;
using System.IO;

namespace WinExplorer.Services;

/// <summary>
/// Finds, describes and (when it is unavoidable) stops other WinExplorer processes, so a
/// stuck copy can be recovered instead of blocking every later launch.
/// </summary>
internal static class WinExplorerProcesses
{
    public const string ProcessName = "WinExplorer";

    /// <summary>All WinExplorer processes except this one.</summary>
    public static IReadOnlyList<Process> Others()
    {
        var others = new List<Process>();
        Process[] all;
        try
        {
            all = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            return others;
        }

        foreach (var process in all)
        {
            if (process.Id == Environment.ProcessId)
            {
                process.Dispose();
                continue;
            }

            others.Add(process);
        }

        return others;
    }

    /// <summary>Stops other instances. Returns how many were terminated.</summary>
    public static int TerminateOthers(Logger? log = null)
    {
        var stopped = 0;
        foreach (var process in Others())
        {
            try
            {
                log?.Warn($"Stopping the unresponsive WinExplorer process {process.Id}.");

                // Ask politely first (a healthy process closes its tray icon and hooks).
                try { process.CloseMainWindow(); } catch { /* ignore */ }
                if (!process.WaitForExit(1200))
                    process.Kill(entireProcessTree: true);

                process.WaitForExit(3000);
                stopped++;
            }
            catch (Exception ex)
            {
                log?.Warn($"Could not stop process {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return stopped;
    }

    /// <summary>One line per process, for the diagnostics report.</summary>
    public static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        Process[] all;
        try
        {
            all = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception ex)
        {
            return new[] { $"  (could not enumerate processes: {ex.Message})" };
        }

        foreach (var process in all)
        {
            try
            {
                var started = "unknown";
                try { started = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { /* access */ }

                var path = "unknown";
                try { path = process.MainModule?.FileName ?? "unknown"; } catch { /* access */ }

                var responding = "unknown";
                try { responding = process.Responding ? "yes" : "no"; } catch { /* access */ }

                var self = process.Id == Environment.ProcessId ? " (this process)" : string.Empty;
                lines.Add($"  pid {process.Id,-6} started {started}  responding {responding,-7} {path}{self}");
            }
            finally
            {
                process.Dispose();
            }
        }

        if (lines.Count == 0)
            lines.Add("  (no WinExplorer process)");

        return lines;
    }
}
