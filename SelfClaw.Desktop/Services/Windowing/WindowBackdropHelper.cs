using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SelfClaw.Desktop.Services;

internal static class WindowBackdropHelper
{
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

    private static bool TrySetBackdrop(IntPtr handle, int backdropType)
    {
        var backdrop = backdropType;
        return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }
}
