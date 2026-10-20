using Microsoft.Win32;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>Reads what Windows itself would show when you press Win+E.</summary>
public static class ShellDefaults
{
    /// <summary>
    /// Returns the shell namespace path Explorer opens by default, honouring
    /// "Open File Explorer to" in Folder Options.
    /// </summary>
    public static string GetLaunchLocation()
    {
        try
        {
            using var advanced = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var launchTo = Convert.ToInt32(advanced?.GetValue("LaunchTo") ?? 1);

            return launchTo switch
            {
                2 => PathNormalizer.Home,
                3 => "shell:::{088E3905-0323-4B02-9826-5D99428E115F}", // Downloads
                4 => "shell:::{018D5C66-4533-4307-9B53-224DE2ED1FE6}", // OneDrive
                _ => PathNormalizer.ThisPc
            };
        }
        catch
        {
            return PathNormalizer.ThisPc;
        }
    }

    /// <summary>The user's Desktop folder, used for a friendly first-run demo.</summary>
    public static string DesktopFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
}
