using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SelfClaw.Desktop.Services.Windowing;

internal static class WindowDwmAttributes
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int Windows11BuildNumber = 22000;

    public static bool IsWindows11OrGreater => Environment.OSVersion.Version.Build >= Windows11BuildNumber;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    // Win11 的圆角统一交给 DWM：内容、系统背景（ThemeMode）与窗口阴影被裁成同一个形状。
    // Win10 没有 DWM 圆角，也不走窗口区域（无抗锯齿、圆角毛糙），保持直角。
    public static void TryApplyRoundedCorners(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (Environment.OSVersion.Version.Build < Windows11BuildNumber)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    public static void TryApplyCaptionTheme(Window window, bool useDarkMode)
    {
        ArgumentNullException.ThrowIfNull(window);

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
}
