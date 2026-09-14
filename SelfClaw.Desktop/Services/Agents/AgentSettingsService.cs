using SelfClaw.Desktop.Services.Agents.Models;
using SelfClaw.Desktop.Services.Agents.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents.Definitions;

namespace SelfClaw.Desktop.Services.Agents;

internal sealed class AgentSettingsService
{
    private readonly DesktopAgentDefinitionService _agents;
    private readonly SubagentDefinitionCatalog _subagents;
    private readonly IExtensionSettingsService _extensions;
    private readonly IExtensionStateChangeNotifier _changes;
    private readonly ILogger<AgentSettingsService> _logger;
    private readonly object _mutationGate = new();

    public AgentSettingsService(DesktopAgentDefinitionService agents, SubagentDefinitionCatalog subagents,
        IExtensionSettingsService extensions, IExtensionStateChangeNotifier changes, ILogger<AgentSettingsService>? logger = null)
    {
        _agents = agents;
        _subagents = subagents;
        _extensions = extensions;
        _changes = changes;
        _logger = logger ?? NullLogger<AgentSettingsService>.Instance;
    }

    public event Action? Changed;
    public long Revision => _changes.CurrentRevision;
    public IReadOnlyList<DesktopAgentDefinition> ListAgents() => _agents.LoadAll();

    public async Task<AgentSettingsState> GetStateAsync(CancellationToken cancellationToken)
    {
        var extensions = await _extensions.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return new(_changes.AdvanceTo(extensions.Revision), _agents.LoadAll().Select(CreateAgentView).ToArray(),
            _subagents.LoadAll().Select(CreateSubagentView).ToArray(), extensions.Plugins, extensions.Skills, extensions.McpServers);
    }

    public AgentDefinitionView CreateAgent(AgentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        DesktopAgentDefinition saved;
        lock (_mutationGate)
        {
            if (_agents.LoadAll().Any(agent => IdEquals(agent.Id, edit.Id)))
                throw new InvalidOperationException($"Agent with id '{edit.Id}' already exists.");
            saved = _agents.Save(new(edit.Id, edit.Name, edit.Description, edit.Mode, AgentRuntimeDefinition.SystemToolPolicy,
                [], [], [], [], edit.Instructions, string.Empty, false, []));
        }
        NotifyChanged();
        return CreateAgentView(saved);
    }

    public AgentDefinitionView SaveAgent(AgentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        DesktopAgentDefinition saved;
        lock (_mutationGate)
            saved = _agents.Save(FindAgent(edit.Id) with { Name = edit.Name, Description = edit.Description,
                Mode = edit.Mode, Instructions = edit.Instructions });
        NotifyChanged();
        return CreateAgentView(saved);
    }

    public void DeleteAgent(string id)
    {
        lock (_mutationGate)
        {
            var agent = FindAgent(id);
            if (agent.IsBuiltIn) throw new InvalidOperationException($"无法删除内置代理 '{agent.Name}'。");
            _agents.Delete(id);
        }
        NotifyChanged();
    }

    public async Task<AgentDefinitionView> SetExtensionBindingAsync(string agentId, ExtensionItemKey key,
        bool enabled, CancellationToken cancellationToken)
    {
        await EnsureExtensionAsync(key, cancellationToken).ConfigureAwait(false);
        DesktopAgentDefinition saved;
        lock (_mutationGate) saved = _agents.SetExtensionBinding(agentId, key, enabled);
        NotifyChanged();
        return CreateAgentView(saved);
    }

    public AgentDefinitionView SetSubagentBinding(string agentId, string subagentId, bool enabled)
    {
        DesktopAgentDefinition saved;
        lock (_mutationGate)
        {
            var subagent = FindSubagent(subagentId);
            var agent = FindAgent(agentId);
            saved = _agents.Save(agent with { SubagentIds = SetListItem(agent.SubagentIds, subagent.Id, enabled) });
        }
        NotifyChanged();
        return CreateAgentView(saved);
    }

    public SubagentDefinitionView CreateSubagent(SubagentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SubagentDefinition saved;
        lock (_mutationGate)
        {
            if (_subagents.Get(edit.Id) is not null) throw new InvalidOperationException($"Subagent with id '{edit.Id}' already exists.");
            saved = _subagents.Save(new(edit.Id, edit.Name, edit.Description, edit.ModelProfileId, edit.ToolPolicy,
                [], [], [], edit.MaxRunSeconds, edit.Instructions, string.Empty, true, []));
        }
        NotifyChanged();
        return CreateSubagentView(saved);
    }

    public SubagentDefinitionView SaveSubagent(SubagentEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SubagentDefinition saved;
        lock (_mutationGate)
            saved = _subagents.Save(FindSubagent(edit.Id) with { Name = edit.Name, Description = edit.Description,
                ModelProfileId = edit.ModelProfileId, ToolPolicy = edit.ToolPolicy, MaxRunSeconds = edit.MaxRunSeconds,
                Instructions = edit.Instructions });
        NotifyChanged();
        return CreateSubagentView(saved);
    }

    public void DeleteSubagent(string id)
    {
        lock (_mutationGate)
        {
            FindSubagent(id);
            _subagents.Delete(id);
        }
        NotifyChanged();
    }

    public async Task<SubagentDefinitionView> SetSubagentExtensionBindingAsync(string subagentId,
        ExtensionItemKey key, bool enabled, CancellationToken cancellationToken)
    {
        await EnsureExtensionAsync(key, cancellationToken).ConfigureAwait(false);
        SubagentDefinition saved;
        lock (_mutationGate)
        {
            var current = FindSubagent(subagentId);
            var updated = key.Kind switch
            {
                ExtensionKind.Plugin => current with { PluginIds = SetListItem(current.PluginIds, key.Id, enabled) },
                ExtensionKind.Skill => current with { SkillIds = SetListItem(current.SkillIds, key.Id, enabled) },
                ExtensionKind.McpServer => current with { McpServerIds = SetListItem(current.McpServerIds, key.Id, enabled) },
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
            saved = _subagents.Save(updated);
        }
        NotifyChanged();
        return CreateSubagentView(saved);
    }

    private void NotifyChanged()
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); }
            catch (Exception exception) { _logger.LogError(exception, "Failed to observe committed agent definitions."); }
        }
        _changes.Advance();
    }

    private DesktopAgentDefinition FindAgent(string id) => _agents.LoadAll().FirstOrDefault(agent => IdEquals(agent.Id, id))
        ?? throw new KeyNotFoundException($"Agent '{id}' was not found.");

    private SubagentDefinition FindSubagent(string id) => _subagents.Get(id)
        ?? throw new KeyNotFoundException($"Subagent '{id}' was not found.");

    private async Task EnsureExtensionAsync(ExtensionItemKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var state = await _extensions.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var exists = key.Kind switch
        {
            ExtensionKind.Plugin => state.Plugins.Any(item => IdEquals(item.Id, key.Id)),
            ExtensionKind.Skill => state.Skills.Any(item => IdEquals(item.Id, key.Id)),
            ExtensionKind.McpServer => state.McpServers.Any(item => IdEquals(item.Id, key.Id)),
            _ => false
        };
        if (!exists) throw new KeyNotFoundException($"{key.Kind} extension '{key.Id}' was not found.");
    }

    private static AgentDefinitionView CreateAgentView(DesktopAgentDefinition agent) => new(agent.Id, agent.Name,
        agent.Description, agent.Mode == AgentExecutionMode.Cli ? "cli" : "direct", agent.PluginIds, agent.SkillIds,
        agent.McpServerIds, agent.SubagentIds, agent.Instructions, agent.IsBuiltIn, agent.Warnings);

    private static SubagentDefinitionView CreateSubagentView(SubagentDefinition subagent) => new(subagent.Id,
        subagent.Name, subagent.Description, subagent.ModelProfileId, subagent.ToolPolicy, subagent.PluginIds,
        subagent.SkillIds, subagent.McpServerIds, subagent.MaxRunSeconds, subagent.Instructions, subagent.IsValid, subagent.Diagnostics);

    private static IReadOnlyList<string> SetListItem(IReadOnlyList<string> values, string id, bool enabled)
    {
        var result = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled) result.Add(id.Trim()); else result.Remove(id.Trim());
        return result.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IdEquals(string left, string right) => string.Equals(left, right.Trim(), StringComparison.OrdinalIgnoreCase);
}
