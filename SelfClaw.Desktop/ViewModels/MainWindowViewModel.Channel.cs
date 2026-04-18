using System.Windows;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task SaveChannelAsync(ChannelEditorResult result)
    {
        await _channelManager.SaveChannelAsync(result);
        StatusText = $"已保存频道“{ResolveChannelName(result.ChannelId)}”的配置。";
        PublishShell(false);
    }

    public async Task SetChannelEnabledAsync(string channelId, bool enabled)
    {
        await _channelManager.SetChannelEnabledAsync(channelId, enabled);
        StatusText = enabled
            ? $"已启动频道“{ResolveChannelName(channelId)}”。"
            : $"已停止频道“{ResolveChannelName(channelId)}”。";
        PublishShell(false);
    }

    private async void OnChannelManagerChanged(object? sender, DesktopChannelManagerEvent e)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => OnChannelManagerChanged(sender, e)), DispatcherPriority.Background);
            return;
        }

        await RefreshFromChannelManagerAsync(e);
    }

    private async Task RefreshFromChannelManagerAsync(DesktopChannelManagerEvent e)
    {
        var selectedConversationId = SelectedConversation?.Id;
        await ReloadConversationsAsync();

        if (e.ConversationId is Guid preferredConversationId &&
            SelectedConversationMode == ConversationMode.Channel)
        {
            var preferredConversation = Conversations.FirstOrDefault(item => item.Id == preferredConversationId)
                ?? _allConversations.FirstOrDefault(item => item.Id == preferredConversationId);
            if (preferredConversation is not null)
            {
                SelectedConversation = preferredConversation;
                return;
            }
        }

        if (selectedConversationId is Guid currentConversationId &&
            (SelectedConversationMode == ConversationMode.Channel || e.ConversationId == currentConversationId))
        {
            var updatedConversation = _allConversations.FirstOrDefault(item => item.Id == currentConversationId);
            if (updatedConversation is not null)
            {
                await LoadConversationAsync(updatedConversation);
                return;
            }
        }

        PublishShell(false);
    }

    private string ResolveChannelName(string channelId)
        => _channelManager.GetChannelName(channelId);
}
