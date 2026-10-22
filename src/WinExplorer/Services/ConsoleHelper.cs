using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinExplorer.Services;

/// <summary>
/// Lets a <c>WinExe</c> write to the console it was started from (cmd, Windows Terminal,
/// a script). Falls back to a message box when there is no console.
/// </summary>
public static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static bool _attached;

    /// <summary>True when output can go to a real console.</summary>
    public static bool TryAttach()
    {
        if (_attached)
            return true;

        try
        {
            if (Environment.UserInteractive && AttachConsole(AttachParentProcess))
            {
                _attached = true;
                return true;
            }
        }
        catch
        {
            // no console available
        }

        return false;
    }

    public static void WriteLine(string text)
    {
        if (TryAttach())
        {
            try
            {
                var stdout = Console.OpenStandardOutput();
                using var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine(text);
                return;
            }
            catch
            {
                // fall through to the message box
            }
        }

        System.Windows.MessageBox.Show(text, "WinExplorer",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    public static void Detach()
    {
        if (!_attached)
            return;

        try { FreeConsole(); } catch { /* ignore */ }
        _attached = false;
    }
}
