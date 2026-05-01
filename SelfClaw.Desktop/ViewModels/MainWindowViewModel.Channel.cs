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

    public Task SaveMcpServerAsync(McpServerEditorResult result)
    {
        var serverId = NormalizeMcpServerId(result.ServerId);
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("MCP server id is required.");
        }

        if (!IsValidMcpServerId(serverId))
        {
            throw new InvalidOperationException("MCP server id can only contain letters, numbers, dots, underscores, and hyphens.");
        }

        var command = result.Command.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("MCP server command is required.");
        }

        var settings = _desktopSettingsStore.Load();
        var servers = settings.McpServers.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        servers[serverId] = new DesktopMcpServerConfiguration
        {
            Enabled = result.Enabled,
            DisplayName = result.DisplayName.Trim(),
            Command = command,
            Args = result.Args
                .Select(item => item?.Trim() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray(),
            Env = result.Env
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .ToDictionary(
                    item => item.Key.Trim(),
                    item => item.Value?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
        };

        _desktopSettingsStore.Save(settings with { McpServers = servers });
        _desktopSettings = _desktopSettingsStore.Load();
        StatusText = $"Saved MCP server '{serverId}'.";
        PublishShell(false);
        return Task.CompletedTask;
    }

    public Task DeleteMcpServerAsync(string serverId)
    {
        serverId = NormalizeMcpServerId(serverId);
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("MCP server id is required.");
        }

        var settings = _desktopSettingsStore.Load();
        var servers = settings.McpServers.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (!servers.Remove(serverId))
        {
            return Task.CompletedTask;
        }

        _desktopSettingsStore.Save(settings with { McpServers = servers });
        _desktopSettings = _desktopSettingsStore.Load();
        StatusText = $"Deleted MCP server '{serverId}'.";
        PublishShell(false);
        return Task.CompletedTask;
    }

    private IReadOnlyList<TranscriptMcpServerItem> BuildTranscriptMcpServers()
        => (_desktopSettings.McpServers ?? new Dictionary<string, DesktopMcpServerConfiguration>())
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var configuration = item.Value ?? DesktopMcpServerConfiguration.Default;
                var displayName = string.IsNullOrWhiteSpace(configuration.DisplayName)
                    ? item.Key
                    : configuration.DisplayName.Trim();

                return new TranscriptMcpServerItem(
                    item.Key,
                    displayName,
                    configuration.Enabled,
                    configuration.Command ?? string.Empty,
                    configuration.Args?.ToArray() ?? [],
                    configuration.Env?.ToDictionary(env => env.Key, env => env.Value, StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            })
            .ToArray();

    private static string NormalizeMcpServerId(string? serverId)
        => serverId?.Trim() ?? string.Empty;

    private static bool IsValidMcpServerId(string serverId)
        => serverId.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');

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
