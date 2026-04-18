using System.Windows;
using SelfClaw.Desktop.Notifications;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopNotificationService : IDisposable
{
    private const double ScreenPadding = 16;
    private const double VerticalSpacing = 12;

    private readonly List<DesktopNotificationWindow> _windows = [];
    private Window? _mainWindow;

    public void RegisterMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void ShowConversationCompleted(string title, string message)
        => ShowCore(
            title,
            message,
            primaryActionLabel: null,
            primaryAction: null,
            secondaryActionLabel: null,
            secondaryAction: null,
            replyAction: null,
            autoCloseAfter: TimeSpan.FromSeconds(8));

    public void ShowConversationCompleted(
        string title,
        string message,
        Func<string, Task> replyAction)
        => ShowCore(
            title,
            message,
            primaryActionLabel: null,
            primaryAction: null,
            secondaryActionLabel: null,
            secondaryAction: null,
            replyAction: replyAction,
            autoCloseAfter: null);

    public void ShowToolApproval(
        string title,
        string message,
        Func<Task> confirmAction,
        Func<Task> cancelAction)
        => ShowCore(
            title,
            message,
            primaryActionLabel: "Confirm",
            primaryAction: confirmAction,
            secondaryActionLabel: "Cancel",
            secondaryAction: cancelAction,
            replyAction: null,
            autoCloseAfter: null);

    private void ShowCore(
        string title,
        string message,
        string? primaryActionLabel,
        Func<Task>? primaryAction,
        string? secondaryActionLabel,
        Func<Task>? secondaryAction,
        Func<string, Task>? replyAction,
        TimeSpan? autoCloseAfter)
    {
        if (System.Windows.Application.Current is null)
        {
            return;
        }

        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            ShowCoreOnUiThread(
                title,
                message,
                primaryActionLabel,
                primaryAction,
                secondaryActionLabel,
                secondaryAction,
                replyAction,
                autoCloseAfter);
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            ShowCoreOnUiThread(
                title,
                message,
                primaryActionLabel,
                primaryAction,
                secondaryActionLabel,
                secondaryAction,
                replyAction,
                autoCloseAfter)));
    }

    private void ShowCoreOnUiThread(
        string title,
        string message,
        string? primaryActionLabel,
        Func<Task>? primaryAction,
        string? secondaryActionLabel,
        Func<Task>? secondaryAction,
        Func<string, Task>? replyAction,
        TimeSpan? autoCloseAfter)
    {
        if (!ShouldShowNotification())
        {
            return;
        }

        var notificationWindow = new DesktopNotificationWindow(
            title,
            message,
            primaryActionLabel,
            primaryAction,
            secondaryActionLabel,
            secondaryAction,
            replyAction,
            autoCloseAfter);

        notificationWindow.Loaded += OnNotificationWindowLoaded;
        notificationWindow.Closed += OnNotificationWindowClosed;
        _windows.Add(notificationWindow);
        notificationWindow.Show();
    }

    private bool ShouldShowNotification()
    {
        var mainWindow = _mainWindow;
        if (mainWindow is null)
        {
            return true;
        }

        return !mainWindow.IsVisible ||
               mainWindow.Visibility != Visibility.Visible ||
               mainWindow.WindowState == WindowState.Minimized;
    }

    private void OnNotificationWindowLoaded(object sender, RoutedEventArgs e)
    {
        RepositionWindows();
    }

    private void OnNotificationWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not DesktopNotificationWindow notificationWindow)
        {
            return;
        }

        notificationWindow.Loaded -= OnNotificationWindowLoaded;
        notificationWindow.Closed -= OnNotificationWindowClosed;
        _windows.Remove(notificationWindow);
        RepositionWindows();
    }

    private void RepositionWindows()
    {
        var loadedWindows = _windows
            .Where(window => window.IsLoaded)
            .ToArray();

        if (loadedWindows.Length == 0)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var bottom = workArea.Bottom - ScreenPadding;

        foreach (var window in loadedWindows.Reverse())
        {
            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

            window.Left = workArea.Right - width - ScreenPadding;
            window.Top = Math.Max(workArea.Top + ScreenPadding, bottom - height);
            bottom = window.Top - VerticalSpacing;
        }
    }

    public void Dispose()
    {
        foreach (var window in _windows.ToArray())
        {
            window.Loaded -= OnNotificationWindowLoaded;
            window.Closed -= OnNotificationWindowClosed;
            window.Close();
        }

        _windows.Clear();
    }
}
