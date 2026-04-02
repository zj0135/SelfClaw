using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SelfClaw.Desktop.Services;

internal static class WindowBackdropHelper
{
    private const int DwmwaSystemBackdropType = 38;
    private const int MainWindowBackdrop = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void TryApplyMica(Window window)
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

        var backdrop = MainWindowBackdrop;
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
    }
}