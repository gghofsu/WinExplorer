using System.Drawing;
using System.Windows.Forms;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>The notification area icon and its menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Logger _log;
    private bool _disposed;

    public TrayIcon(Logger log, Action openSettings, Action toggleEnabled, Action duplicateTab,
        Action reopenClosed, Action openLog, Action exit)
    {
        _log = log;

        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => toggleEnabled())
        {
            CheckOnClick = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("WinExplorer settings…", null, (_, _) => openSettings())
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold)
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Duplicate active tab", null, (_, _) => duplicateTab()));
        menu.Items.Add(new ToolStripMenuItem("Reopen closed folder", null, (_, _) => reopenClosed()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open log file", null, (_, _) => openLog()));
        menu.Items.Add(new ToolStripMenuItem("Exit WinExplorer", null, (_, _) => exit()));

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "WinExplorer — folders open as tabs",
            ContextMenuStrip = menu,
            Icon = LoadIcon()
        };

        _icon.DoubleClick += (_, _) => openSettings();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var extracted = Icon.ExtractAssociatedIcon(AppPaths.ExecutablePath);
            if (extracted is not null)
                return extracted;
        }
        catch
        {
            // fall back to a system icon
        }

        return SystemIcons.Application;
    }

    public void UpdateState(bool enabled)
    {
        _pauseItem.Text = enabled ? "Pause WinExplorer" : "Resume WinExplorer";
        _icon.Text = enabled
            ? "WinExplorer — active"
            : "WinExplorer — paused";
    }

    public void ShowBalloon(string message, string title = "WinExplorer")
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(2500);
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not show a notification: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
