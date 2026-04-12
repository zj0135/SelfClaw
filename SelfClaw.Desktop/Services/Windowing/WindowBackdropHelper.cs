using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SelfClaw.Desktop.Services;

internal static class WindowBackdropHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaSystemBackdropType = 38;
    private const int MainWindowBackdrop = 2;
    private const int TransientWindowBackdrop = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void TryApplySystemBackdrop(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22523)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (TrySetBackdrop(handle, TransientWindowBackdrop))
        {
            return;
        }

        _ = TrySetBackdrop(handle, MainWindowBackdrop);
    }

    public static void TryApplyMica(Window window)
    {
        TryApplySystemBackdrop(window);
    }

    public static void TryApplyCaptionTheme(Window window, bool useDarkMode)
    {
        if (Environment.OSVersion.Version.Build < 17763)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = useDarkMode ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) == 0)
        {
            return;
        }

        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
    }

    private static bool TrySetBackdrop(IntPtr handle, int backdropType)
    {
        var backdrop = backdropType;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }
}
