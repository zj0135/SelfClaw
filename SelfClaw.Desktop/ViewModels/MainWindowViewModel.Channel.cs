using System.IO;
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

    public Task SetMcpServerEnabledAsync(string serverId, bool enabled)
    {
        serverId = NormalizeMcpServerId(serverId);
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("MCP server id is required.");
        }

        var settings = _desktopSettingsStore.Load();
        var servers = settings.McpServers.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (!servers.TryGetValue(serverId, out var configuration))
        {
            throw new InvalidOperationException($"MCP server '{serverId}' was not found.");
        }

        servers[serverId] = (configuration ?? DesktopMcpServerConfiguration.Default) with
        {
            Enabled = enabled
        };

        _desktopSettingsStore.Save(settings with { McpServers = servers });
        _desktopSettings = _desktopSettingsStore.Load();
        StatusText = enabled ? $"Enabled MCP server '{serverId}'." : $"Disabled MCP server '{serverId}'.";
        PublishShell(false);
        return Task.CompletedTask;
    }

    public Task SetSkillEnabledAsync(string skillId, bool enabled)
    {
        skillId = NormalizeSkillId(skillId);
        if (string.IsNullOrWhiteSpace(skillId))
        {
            throw new InvalidOperationException("Skill id is required.");
        }

        var settings = _desktopSettingsStore.Load();
        var disabledSkills = new HashSet<string>(
            (settings.DisabledSkills ?? [])
                .Select(NormalizeSkillId)
                .Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);

        if (enabled)
        {
            disabledSkills.Remove(skillId);
        }
        else
        {
            disabledSkills.Add(skillId);
        }

        _desktopSettingsStore.Save(settings with
        {
            DisabledSkills = disabledSkills
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        });

        _desktopSettings = _desktopSettingsStore.Load();
        StatusText = enabled ? $"Enabled skill '{skillId}'." : $"Disabled skill '{skillId}'.";
        PublishShell(false);
        return Task.CompletedTask;
    }

    private IReadOnlyList<TranscriptMcpServerItem> BuildTranscriptMcpServers(
        IReadOnlyList<TranscriptMcpServerItem>? availableServers = null)
    {
        var selectedServerIds = ResolveSelectedAgent()
            .EnabledMcpServers
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (availableServers ?? BuildAvailableTranscriptMcpServers())
            .Where(item => item.Enabled && selectedServerIds.Contains(item.Id))
            .ToArray();
    }

    private IReadOnlyList<TranscriptMcpServerItem> BuildAvailableTranscriptMcpServers()
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

    private IReadOnlyList<TranscriptSkillItem> BuildTranscriptSkills(
        IReadOnlyList<TranscriptSkillItem>? availableSkills = null)
    {
        var selectedSkillIds = ResolveSelectedAgent()
            .EnabledSkills
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (availableSkills ?? BuildAvailableTranscriptSkills())
            .Where(item => item.Enabled && selectedSkillIds.Contains(item.Id))
            .ToArray();
    }

    private IReadOnlyList<TranscriptSkillItem> BuildAvailableTranscriptSkills()
    {
        var skillsRoot = Path.Combine(_storagePaths.AppDataDirectory, "skills");
        if (!Directory.Exists(skillsRoot))
        {
            return [];
        }

        var disabledSkills = new HashSet<string>(
            (_desktopSettings.DisabledSkills ?? [])
                .Select(NormalizeSkillId)
                .Where(item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);

        string[] skillFiles;
        try
        {
            skillFiles = Directory
                .EnumerateFiles(
                    skillsRoot,
                    "SKILL.md",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive
                    })
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return skillFiles
            .Select(skillFilePath => BuildTranscriptSkillItem(skillsRoot, skillFilePath, disabledSkills))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TranscriptSkillItem? BuildTranscriptSkillItem(
        string skillsRoot,
        string skillFilePath,
        ISet<string> disabledSkills)
    {
        var skillDirectory = Path.GetDirectoryName(skillFilePath);
        if (string.IsNullOrWhiteSpace(skillDirectory))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(skillsRoot, skillDirectory);
        var skillId = NormalizeSkillId(relativePath == "." ? new DirectoryInfo(skillDirectory).Name : relativePath);
        if (string.IsNullOrWhiteSpace(skillId))
        {
            return null;
        }

        string markdown;
        try
        {
            markdown = File.ReadAllText(skillFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            markdown = $"Unable to read SKILL.md: {exception.Message}";
        }

        var name = ResolveSkillName(markdown, new DirectoryInfo(skillDirectory).Name);
        return new TranscriptSkillItem(
            skillId,
            name,
            skillId,
            skillFilePath,
            markdown,
            !disabledSkills.Contains(skillId));
    }

    private static string ResolveSkillName(string markdown, string fallback)
    {
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('#'))
            {
                continue;
            }

            var title = trimmed.TrimStart('#').Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return fallback;
    }

    private static string NormalizeMcpServerId(string? serverId)
        => serverId?.Trim() ?? string.Empty;

    private static string NormalizeSkillId(string? skillId)
    {
        var normalized = (skillId ?? string.Empty).Replace('\\', '/').Trim('/');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item is not "." and not "..")
            .ToArray();

        return string.Join("/", segments);
    }

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
