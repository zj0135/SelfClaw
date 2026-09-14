using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.ViewModels;

namespace SelfClaw.Desktop.Services.Windowing;

internal sealed class DesktopConversationActivationService
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IConversationRepository _conversations;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<DesktopConversationActivationService> _logger;
    private Window? _window;
    private int _generation;

    public DesktopConversationActivationService(MainWindowViewModel viewModel, IConversationRepository conversations,
        WebViewHostChannel channel, Dispatcher dispatcher, ILogger<DesktopConversationActivationService> logger)
    {
        _viewModel = viewModel;
        _conversations = conversations;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public void RegisterMainWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public async Task<bool> ActivateAsync(Guid? conversationId = null)
    {
        if (_dispatcher.HasShutdownStarted) return false;
        if (!_dispatcher.CheckAccess())
            return await (await _dispatcher.InvokeAsync(() => ActivateAsync(conversationId)));
        var generation = ++_generation;
        if (_window is { } window)
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Focus();
        }
        if (conversationId is not Guid id) return true;
        try
        {
            await _viewModel.InitializeAsync();
            var conversation = await _conversations.GetConversationAsync(id)
                ?? throw new InvalidOperationException("该会话已删除，无法打开。");
            var parentId = conversation.Kind == ConversationKind.Subagent ? conversation.ParentConversationId : conversation.Id;
            if (parentId is not Guid target) throw new InvalidOperationException("该通知没有可打开的交互会话。");
            if (generation != _generation) return false;
            await _viewModel.SelectConversationAsync(target);
            _channel.PostPush(new { type = "conversation-activated", conversationId = target });
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not activate conversation {ConversationId}.", conversationId);
            _channel.PostPush(new { type = "operation-result", command = "open-conversation", ok = false,
                errorCode = "conversation-unavailable", error = exception.Message });
            return false;
        }
    }
}
