using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Task SetSelectedAgentAsync(string? agentId)
    {
        SelectAgentCore(agentId, publishShell: true, applyFilter: true);
        return Task.CompletedTask;
    }

    public async Task AssignSelectedConversationAgentAsync(string? agentId)
    {
        if (SelectedConversation is not { } conversation ||
            conversation.Mode != ConversationMode.Programming)
        {
            return;
        }

        var agent = ResolveAgent(agentId);
        if (string.Equals(conversation.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectAgentCore(agent.Id, publishShell: true, applyFilter: true);
            return;
        }

        var updated = conversation with
        {
            AgentId = agent.Id,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        if (_conversationRuntimeStates.TryGetValue(updated.Id, out var runtimeState))
        {
            runtimeState.Conversation = updated;
        }

        await PersistConversationAsync(updated);
        SelectAgentCore(agent.Id, publishShell: false, applyFilter: false);
        RefreshPlanningModeForSelection(publishShell: false);
        ApplyConversationFilter(updated.Id);
        StatusText = $"Switched conversation to agent '{agent.Name}'.";
    }

    public async Task SaveAgentAsync(DesktopAgentEditorResult result)
    {
        var originalAgentId = NormalizeAgentId(result.OriginalAgentId);
        var savedAgent = _desktopAgentStore.Save(result);

        if (!string.IsNullOrWhiteSpace(originalAgentId) &&
            !string.Equals(originalAgentId, savedAgent.Id, StringComparison.OrdinalIgnoreCase))
        {
            await RebindConversationsAsync(originalAgentId, savedAgent.Id);
        }

        await ReloadAgentsAsync(savedAgent.Id);
        await ReloadConversationsAsync();
        ApplyConversationFilter(SelectedConversation?.Id);
        StatusText = $"Saved agent '{savedAgent.Name}'.";
    }

    public async Task DeleteAgentAsync(string? agentId)
    {
        var normalizedAgentId = NormalizeAgentId(agentId);
        if (string.IsNullOrWhiteSpace(normalizedAgentId))
        {
            throw new InvalidOperationException("Agent id is required.");
        }

        if (_allConversations.Any(item =>
                item.Mode == ConversationMode.Programming &&
                string.Equals(item.AgentId, normalizedAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Agent '{normalizedAgentId}' is still used by one or more conversations.");
        }

        _desktopAgentStore.Delete(normalizedAgentId);
        await ReloadAgentsAsync(string.Equals(_selectedAgentId, normalizedAgentId, StringComparison.OrdinalIgnoreCase)
            ? DesktopAgentStore.BuildAgentId
            : _selectedAgentId);
        ApplyConversationFilter(SelectedConversation?.Id);
        StatusText = $"Deleted agent '{normalizedAgentId}'.";
    }

    public async Task ReloadAgentsAsync(string? preferredAgentId = null)
    {
        _agents.Clear();
        _agents.AddRange(_desktopAgentStore.LoadAll());
        SelectAgentCore(preferredAgentId, publishShell: false, applyFilter: false);
        RefreshPlanningModeForSelection(publishShell: false);
        await Task.CompletedTask;
    }

    public Task SetPlanningModeAsync(bool enabled)
    {
        var targetAgentId = enabled
            ? DesktopAgentStore.PlanAgentId
            : DesktopAgentStore.BuildAgentId;

        if (SelectedConversation is { Mode: ConversationMode.Programming })
        {
            return AssignSelectedConversationAgentAsync(targetAgentId);
        }

        return SetSelectedAgentAsync(targetAgentId);
    }

    private async Task RebindConversationsAsync(string originalAgentId, string nextAgentId)
    {
        var conversations = await _conversationRepository.ListConversationsAsync();
        foreach (var conversation in conversations.Where(item =>
                     item.Mode == ConversationMode.Programming &&
                     string.Equals(item.AgentId, originalAgentId, StringComparison.OrdinalIgnoreCase)))
        {
            var updated = conversation with
            {
                AgentId = nextAgentId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await _conversationRepository.UpsertConversationAsync(updated);
            if (_conversationRuntimeStates.TryGetValue(updated.Id, out var runtimeState))
            {
                runtimeState.Conversation = updated;
            }
        }
    }

    private IReadOnlyList<TranscriptAgentItem> BuildTranscriptAgents()
        => _agents
            .OrderBy(item => item.IsBuiltIn ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new TranscriptAgentItem(
                item.Id,
                item.Name,
                item.Description,
                AgentModeToId(item.Mode),
                item.ToolPolicy,
                item.Skills.ToArray(),
                item.DisabledSkills.ToArray(),
                item.McpServers.ToArray(),
                item.DisabledMcpServers.ToArray(),
                item.Instructions,
                item.FilePath,
                item.IsBuiltIn,
                item.Warnings.ToArray()))
            .ToArray();

    private DesktopAgentDefinition ResolveSelectedAgent()
        => ResolveAgent(_selectedAgentId);

    private DesktopAgentDefinition ResolveAgent(string? agentId)
    {
        var normalizedAgentId = NormalizeAgentId(agentId);
        return _agents.FirstOrDefault(item => string.Equals(item.Id, normalizedAgentId, StringComparison.OrdinalIgnoreCase))
               ?? _agents.FirstOrDefault(item => string.Equals(item.Id, DesktopAgentStore.BuildAgentId, StringComparison.OrdinalIgnoreCase))
               ?? _agents.First();
    }

    private void SelectAgentCore(string? agentId, bool publishShell, bool applyFilter)
    {
        var nextAgent = ResolveAgent(agentId);
        var changed = !string.Equals(_selectedAgentId, nextAgent.Id, StringComparison.OrdinalIgnoreCase);
        _selectedAgentId = nextAgent.Id;
        RefreshPlanningModeForSelection(publishShell: false);

        if (applyFilter && SelectedConversationMode == ConversationMode.Programming)
        {
            ApplyConversationFilter(SelectedConversation?.Id);
        }

        if (publishShell && (changed || !applyFilter))
        {
            PublishShell(false);
        }
    }

    private void SyncSelectedAgentFromConversation(ConversationRecord conversation, bool publishShell)
    {
        if (conversation.Mode != ConversationMode.Programming)
        {
            RefreshPlanningModeForSelection(publishShell);
            return;
        }

        SelectAgentCore(conversation.AgentId, publishShell, applyFilter: false);
    }

    private void RefreshPlanningModeForSelection(bool publishShell)
    {
        var enabled = SelectedConversationMode == ConversationMode.Programming &&
                      ResolveSelectedAgent().Mode == AgentExecutionMode.Plan;
        if (SetProperty(ref _isPlanningModeEnabled, enabled, nameof(IsPlanningModeEnabled)) && publishShell)
        {
            PublishShell(false);
        }
    }

    private AgentRuntimeDefinition ResolveRuntimeAgent(ConversationMode mode, string? agentId)
    {
        if (mode == ConversationMode.Channel)
        {
            return new AgentRuntimeDefinition(
                DesktopAgentStore.BuildAgentId,
                DesktopAgentStore.BuildAgentId,
                "通用代理（默认）",
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                string.Empty);
        }

        var agent = ResolveAgent(agentId);
        var resolvedMcpServers = ResolveGloballyEnabledMcpServers(agent.EnabledMcpServers);
        return new AgentRuntimeDefinition(
            agent.Id,
            agent.Name,
            agent.Description,
            agent.Mode,
            agent.ToolPolicy,
            ResolveGloballyEnabledSkills(agent.EnabledSkills),
            resolvedMcpServers.Select(item => item.Id).ToArray(),
            resolvedMcpServers,
            agent.Instructions);
    }

    private string? ResolveConversationAgentName(ConversationRecord conversation)
        => conversation.Mode == ConversationMode.Programming
            ? ResolveAgent(conversation.AgentId).Name
            : null;

    private static string NormalizeAgentId(string? agentId)
        => agentId?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string AgentModeToId(AgentExecutionMode mode)
        => mode == AgentExecutionMode.Plan ? "plan" : "direct";

    private IReadOnlyList<string> ResolveGloballyEnabledSkills(IReadOnlyList<string> skillIds)
    {
        if (skillIds.Count == 0)
        {
            return [];
        }

        var disabledSkills = (_desktopSettings.DisabledSkills ?? [])
            .Select(NormalizeSkillId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (disabledSkills.Count == 0)
        {
            return skillIds.ToArray();
        }

        return skillIds
            .Where(item => !disabledSkills.Contains(NormalizeSkillId(item)))
            .ToArray();
    }

    private IReadOnlyList<AgentMcpServerDefinition> ResolveGloballyEnabledMcpServers(IReadOnlyList<string> serverIds)
    {
        if (serverIds.Count == 0)
        {
            return [];
        }

        var configuredServers = (_desktopSettings.McpServers ?? new Dictionary<string, DesktopMcpServerConfiguration>())
            .Where(item =>
                (item.Value ?? DesktopMcpServerConfiguration.Default).Enabled &&
                !string.IsNullOrWhiteSpace(item.Value?.Command))
            .ToDictionary(item => item.Key, item => item.Value ?? DesktopMcpServerConfiguration.Default, StringComparer.OrdinalIgnoreCase);

        return serverIds
            .Where(item => configuredServers.ContainsKey(item))
            .Select(item =>
            {
                var configuration = configuredServers[item];
                return new AgentMcpServerDefinition(
                    item,
                    configuration.DisplayName,
                    configuration.Command,
                    configuration.Args.ToArray(),
                    configuration.Env.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase));
            })
            .ToArray();
    }
}
