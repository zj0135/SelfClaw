using Microsoft.Win32;

namespace SelfClaw.Desktop.Services;

internal static class SystemThemeReader
{
    private const string PersonalizePath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    public static bool IsDarkModeEnabled()
    {
        using var personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizePath);
        if (personalizeKey?.GetValue(AppsUseLightThemeValueName) is int appsUseLightTheme)
        {
            return appsUseLightTheme == 0;
        }

        return false;
    }
}
