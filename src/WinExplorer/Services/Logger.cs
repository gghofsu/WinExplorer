using System.Text;
using WinExplorer.Core;

namespace WinExplorer.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Minimal append-only log. Explorer integration is hard to debug from the outside, so
/// every tab operation is recorded in <c>%APPDATA%\WinExplorer\WinExplorer.log</c>.
/// </summary>
public sealed class Logger
{
    private readonly object _gate = new();
    private readonly string _path;
    private long _size;

    public Logger(string? path = null)
    {
        _path = path ?? AppPaths.LogFile;
        try
        {
            AppPaths.EnsureDataDirectory();
            if (File.Exists(_path))
            {
                _size = new FileInfo(_path).Length;
                if (_size > 2 * 1024 * 1024)
                    Trim();
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public event EventHandler<string>? LineWritten;

    public string LogPath => _path;

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    public void Write(LogLevel level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-7}] {message}";

        lock (_gate)
        {
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                _size += line.Length + Environment.NewLine.Length;
                if (_size > 2 * 1024 * 1024)
                    Trim();
            }
            catch
            {
                // ignore
            }
        }

        LineWritten?.Invoke(this, line);
    }

    /// <summary>Last <paramref name="maxLines"/> lines, newest last.</summary>
    public IReadOnlyList<string> Tail(int maxLines = 400)
    {
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<string>();

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                if (lines.Count > maxLines * 2)
                    lines.RemoveRange(0, lines.Count - maxLines);
            }

            return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void Trim()
    {
        try
        {
            var lines = Tail(400).ToList();
            File.WriteAllText(_path, string.Join(Environment.NewLine, lines) + Environment.NewLine, Encoding.UTF8);
            _size = new FileInfo(_path).Length;
        }
        catch
        {
            // ignore
        }
    }
}
