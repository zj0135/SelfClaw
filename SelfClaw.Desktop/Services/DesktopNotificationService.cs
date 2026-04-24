using System.Windows;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopNotificationService : IDisposable
{
    private readonly ILogger<DesktopNotificationService> _logger;
    private Window? _mainWindow;

    public DesktopNotificationService(ILogger<DesktopNotificationService> logger)
    {
        _logger = logger;
    }

    public void RegisterMainWindow(Window mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void ShowConversationCompleted(Guid conversationId, string title, string message)
    {
        var openArguments = DesktopNotificationArguments.Build(
            (DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.OpenConversationAction),
            (DesktopNotificationArguments.ConversationIdKey, conversationId.ToString()));

        ShowCore(builder =>
        {
            AddRootConversationArguments(builder, conversationId);
            AddNotificationText(builder, title, message);
            builder.AddButton(new ToastButton("Open", openArguments));
        });
    }

    public void ShowToolApproval(Guid toolExecutionId, Guid? conversationId, string title, string message)
    {
        ShowCore(builder =>
        {
            if (conversationId is Guid targetConversationId)
            {
                AddRootConversationArguments(builder, targetConversationId);
            }
            else
            {
                builder.AddArgument(DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.OpenAppAction);
            }

            AddNotificationText(builder, title, message);
            builder.AddButton(
                new ToastButton(
                    "Confirm",
                    DesktopNotificationArguments.Build(
                        (DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.ApproveToolAction),
                        (DesktopNotificationArguments.ToolExecutionIdKey, toolExecutionId.ToString()))));
            builder.AddButton(
                new ToastButton(
                    "Cancel",
                    DesktopNotificationArguments.Build(
                        (DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.RejectToolAction),
                        (DesktopNotificationArguments.ToolExecutionIdKey, toolExecutionId.ToString()))));
        });
    }

    private void ShowCore(Action<ToastContentBuilder> configure)
    {
        if (!ShouldShowNotification())
        {
            return;
        }

        try
        {
            var builder = new ToastContentBuilder();
            configure(builder);
            builder.Show();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to show a Windows toast notification.");
        }
    }

    private static void AddRootConversationArguments(ToastContentBuilder builder, Guid conversationId)
    {
        builder.AddArgument(DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.OpenConversationAction);
        builder.AddArgument(DesktopNotificationArguments.ConversationIdKey, conversationId.ToString());
    }

    private static void AddNotificationText(ToastContentBuilder builder, string title, string message)
    {
        builder.AddText(string.IsNullOrWhiteSpace(title) ? "SelfClaw" : title.Trim());

        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AddText(message.Trim());
        }
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

    public void Dispose()
    {
    }
}
