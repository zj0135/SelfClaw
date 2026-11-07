using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SelfClaw.Desktop.Services.Windowing;

internal static class WindowActivation
{
    // 从托盘/隐藏状态被叫出来的窗口拿不到前台权限：被桌面图标那次点击授予前台权限的是新进程，不是
    // 这个老进程，所以窗口.Show() 加 Window.Activate() 只会让任务栏闪一下。把当前前台线程的输入队列
    // 临时挂到自己身上再 SetForegroundWindow，窗口才真的到最前面。
    public static void BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0
                       && foregroundThread != currentThread
                       && AttachThreadInput(foregroundThread, currentThread, attach: true);

        try
        {
            _ = SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(foregroundThread, currentThread, attach: false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
