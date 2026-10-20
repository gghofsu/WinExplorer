using System.Windows;
using System.Windows.Input;
using WinExplorer.Core;

namespace WinExplorer.UI;

public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app, MainViewModel viewModel)
    {
        InitializeComponent();
        _app = app;
        DataContext = viewModel;

        viewModel.AddActivity("WinExplorer is running.");
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        if (sender is not System.Windows.Controls.TextBox box)
            return;

        e.Handled = true;

        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            Apply(box.Tag as string, HotkeySpec.Empty, viewModel);
            return;
        }

        var spec = WpfKeyMap.FromEvent(e);
        if (spec is null)
            return; // a lone modifier: wait for the real key

        if (!spec.Ctrl && !spec.Alt && !spec.Shift && !spec.Win)
        {
            viewModel.AddActivity("A shortcut needs at least one modifier (Ctrl, Alt, Shift or Win).");
            return;
        }

        Apply(box.Tag as string, spec, viewModel);
    }

    private static void Apply(string? target, HotkeySpec spec, MainViewModel viewModel)
    {
        var text = spec.IsEmpty ? string.Empty : spec.ToString();

        switch (target)
        {
            case "duplicate":
                viewModel.DuplicateHotkey = text;
                break;
            case "reopen":
                viewModel.ReopenClosedHotkey = text;
                break;
            case "toggle":
                viewModel.TogglePauseHotkey = text;
                break;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_app.Settings.MinimizeToTrayOnClose && !_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyHiddenToTray();
        }
    }
}
