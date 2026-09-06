using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// The only seam between the Direct runtime and the extension system: given a turn request, it returns an
/// immutable, disposable snapshot of the capabilities that turn may use. It owns the two-layer switch
/// (installed and globally enabled ∩ bound to this Agent) and delegates each capability kind to its own
/// source.
/// </summary>
internal sealed class DirectTurnCapabilityResolver : IDirectTurnCapabilityResolver
{
    private static readonly IReadOnlyDictionary<string, ToolCallKind> BuiltInToolKinds =
        new Dictionary<string, ToolCallKind>(StringComparer.Ordinal)
        {
            ["list_files"] = ToolCallKind.List,
            ["glob_files"] = ToolCallKind.List,
            ["search_text"] = ToolCallKind.Search,
            ["read_file"] = ToolCallKind.Read,
            ["write_file"] = ToolCallKind.Edit,
            ["edit_file"] = ToolCallKind.Edit,
            ["run_shell_command"] = ToolCallKind.Run
        };

    private readonly WorkspaceAgentToolset _workspaceToolset;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly SkillCapabilitySource _skillSource;
    private readonly PluginCapabilitySource _pluginSource;
    private readonly McpCapabilitySource _mcpSource;
    private readonly SubagentCapabilitySource _subagentSource;

    public DirectTurnCapabilityResolver(
        WorkspaceAgentToolset workspaceToolset,
        IExtensionPackageRepository packageRepository,
        SkillCapabilitySource skillSource,
        PluginCapabilitySource pluginSource,
        McpCapabilitySource mcpSource,
        SubagentCapabilitySource subagentSource)
    {
        _workspaceToolset = workspaceToolset;
        _packageRepository = packageRepository;
        _skillSource = skillSource;
        _pluginSource = pluginSource;
        _mcpSource = mcpSource;
        _subagentSource = subagentSource;
    }

    public async Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var leases = new DirectTurnLeaseScope();
        try
        {
            // On success the returned capability lease owns the scope; on failure this catch is the
            // single place every lease taken during resolution is released - MCP and Plugin alike.
            return await ResolveCoreAsync(request, leases, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await leases.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DirectTurnCapabilityLease> ResolveCoreAsync(
        DirectChatTurnRequest request,
        DirectTurnLeaseScope leases,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new TurnDiagnostics();
        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var effectiveRequest = CreateEffectiveRequest(request, packages, diagnostics);
        var (tools, descriptors) = CreateWorkspaceCapabilities(effectiveRequest, diagnostics);
        ValidateCapturedPackageCeiling(effectiveRequest, packages);
        var installedSkills = packages
            .Where(package => package.Kind == ExtensionKind.Skill)
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var effectiveSkills = installedSkills.Values
            .Where(package => package.IsEnabled &&
                              ExtensionInstallation.IsIntact(package) &&
                              effectiveRequest.Agent.SkillIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var plugins = await _pluginSource.ResolveAsync(
                effectiveRequest.Agent,
                packages,
                effectiveSkills,
                leases,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
        var skills = await _skillSource.ResolveAsync(
                effectiveRequest,
                installedSkills,
                effectiveSkills,
                plugins.Skills,
                diagnostics,
                cancellationToken)
            .ConfigureAwait(false);
        tools.AddRange(skills.Tools);
        foreach (var descriptor in skills.Descriptors)
        {
            descriptors[descriptor.ProviderName] = descriptor;
        }

        var systemInstructions = new List<string>();
        systemInstructions.AddRange(plugins.Instructions);
        systemInstructions.AddRange(skills.Instructions);
        var mcpCapabilities = await _mcpSource.AddToolsAsync(
                effectiveRequest,
                tools,
                descriptors,
                leases,
                diagnostics,
                plugins.PluginRoots,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureRequiredCapabilitiesResolved(effectiveRequest, plugins, skills, mcpCapabilities);
        var effectiveCeiling = CreateEffectiveCeiling(
            effectiveRequest,
            packages,
            plugins,
            skills,
            mcpCapabilities);
        foreach (var (tool, descriptor) in _subagentSource.CreateTools(effectiveRequest, effectiveCeiling))
        {
            tools.Add(tool);
            descriptors.Add(descriptor.ProviderName, descriptor);
        }

        ApplyExecutionToolPolicy(effectiveRequest, tools, descriptors);
        if (diagnostics.Degradations.Count > 0)
        {
            systemInstructions.Add(CapabilitySections.Degradation(diagnostics.Degradations));
        }

        // The policy only earns its tokens once something extension-provided is actually in play.
        if (systemInstructions.Count > 0 || mcpCapabilities.Capabilities.Count > 0)
        {
            systemInstructions.Insert(0, CapabilitySections.Policy);
        }

        return new DirectTurnCapabilityLease(
            systemInstructions,
            tools,
            descriptors,
            skills.MessageAdjustments,
            diagnostics.Messages,
            leases.DisposeAsync);
    }

    private (List<AITool> Tools, Dictionary<string, DirectToolDescriptor> Descriptors) CreateWorkspaceCapabilities(
        DirectChatTurnRequest request,
        TurnDiagnostics diagnostics)
    {
        if (request.ExecutionContext.Origin == DirectTurnOrigin.Interactive && !string.Equals(
                request.Agent.ToolPolicy,
                AgentRuntimeDefinition.SystemToolPolicy,
                StringComparison.Ordinal))
        {
            diagnostics.Info($"Unsupported tool policy '{request.Agent.ToolPolicy}' was treated as 'system'.");
        }

        var tools = request.WorkspaceRoot is null
            ? []
            : _workspaceToolset.CreateTools(
                request.WorkspaceRoot,
                request.ConversationId,
                request.ToolPermissionMode,
                request.ToolApprovalHandler).ToList();
        var descriptors = BuiltInToolKinds.ToDictionary(
            kind => kind.Key,
            kind => new DirectToolDescriptor(kind.Key, kind.Value, ToolSourceKind.BuiltIn),
            StringComparer.Ordinal);
        return (tools, descriptors);
    }

    private static void ValidateCapturedPackageCeiling(
        DirectChatTurnRequest request,
        IReadOnlyList<ExtensionPackageRecord> packages)
    {
        if (request.ExecutionContext.Origin != DirectTurnOrigin.Subagent)
        {
            return;
        }

        var ceiling = request.ExecutionContext.CapabilityCeiling
            ?? throw new InvalidDataException("A non-interactive Direct turn requires a capability ceiling.");
        if (ToolPolicyRank(request.Agent.ToolPolicy) > ToolPolicyRank(ceiling.ToolPolicy))
        {
            throw new InvalidDataException("The requested tool policy exceeds the captured capability ceiling.");
        }

        foreach (var pluginId in request.Agent.PluginIds)
        {
            ValidatePackageCapability(packages, ExtensionKind.Plugin, pluginId, ceiling.Plugins);
        }

        foreach (var skillId in request.Agent.SkillIds.Where(id => !id.Contains('/')))
        {
            ValidatePackageCapability(packages, ExtensionKind.Skill, skillId, ceiling.Skills);
        }
    }

    private static DirectChatTurnRequest CreateEffectiveRequest(
        DirectChatTurnRequest request,
        IReadOnlyList<ExtensionPackageRecord> packages,
        TurnDiagnostics diagnostics)
    {
        if (request.ExecutionContext.Origin != DirectTurnOrigin.Continuation)
        {
            return request;
        }

        var ceiling = request.ExecutionContext.CapabilityCeiling
            ?? throw new InvalidDataException("A continuation Direct turn requires a capability ceiling.");
        var pluginIds = FilterContinuationPackages(
            request.Agent.PluginIds,
            ExtensionKind.Plugin,
            ceiling.Plugins,
            packages,
            diagnostics,
            "Plugin");
        var skillIds = request.Agent.SkillIds
            .Where(skillId => IsContinuationSkillAvailable(
                skillId,
                ceiling.Skills,
                packages,
                diagnostics))
            .ToArray();
        var mcpServerIds = request.Agent.McpServerIds
            .Where(id => Contains(ceiling.McpServers.Select(capability => capability.Id), id))
            .ToArray();
        foreach (var removed in request.Agent.McpServerIds.Except(mcpServerIds, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Degrade($"MCP server '{removed}' was removed by the captured capability ceiling.");
        }

        var subagentIds = request.Agent.SubagentIds
            .Where(id => Contains(ceiling.SubagentIds, id))
            .ToArray();
        var toolPolicy = ToolPolicyRank(request.Agent.ToolPolicy) <= ToolPolicyRank(ceiling.ToolPolicy)
            ? request.Agent.ToolPolicy
            : ceiling.ToolPolicy;
        return request with
        {
            Agent = request.Agent with
            {
                ToolPolicy = toolPolicy,
                PluginIds = pluginIds,
                SkillIds = skillIds,
                McpServerIds = mcpServerIds,
                SubagentIds = subagentIds
            }
        };
    }

    private static IReadOnlyList<string> FilterContinuationPackages(
        IReadOnlyList<string> requestedIds,
        ExtensionKind kind,
        IReadOnlyList<DirectExtensionCapability> ceiling,
        IReadOnlyList<ExtensionPackageRecord> packages,
        TurnDiagnostics diagnostics,
        string displayKind)
    {
        var effective = new List<string>();
        foreach (var id in requestedIds)
        {
            var captured = ceiling.FirstOrDefault(capability =>
                string.Equals(capability.Id, id, StringComparison.OrdinalIgnoreCase));
            var package = packages.FirstOrDefault(item =>
                item.Kind == kind && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (captured is not null && IsCapturedPackageCurrent(package, captured))
            {
                effective.Add(id);
            }
            else
            {
                diagnostics.Degrade(
                    $"{displayKind} '{id}' was removed because it is unavailable or changed since delegation.");
            }
        }

        return effective;
    }

    private static bool IsContinuationSkillAvailable(
        string skillId,
        IReadOnlyList<DirectExtensionCapability> ceiling,
        IReadOnlyList<ExtensionPackageRecord> packages,
        TurnDiagnostics diagnostics)
    {
        var captured = ceiling.FirstOrDefault(capability =>
            string.Equals(capability.Id, skillId, StringComparison.OrdinalIgnoreCase));
        ExtensionPackageRecord? package;
        var separatorIndex = skillId.IndexOf('/');
        if (separatorIndex > 0)
        {
            var pluginId = skillId[..separatorIndex];
            package = packages.FirstOrDefault(item =>
                item.Kind == ExtensionKind.Plugin &&
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            package = packages.FirstOrDefault(item =>
                item.Kind == ExtensionKind.Skill &&
                string.Equals(item.Id, skillId, StringComparison.OrdinalIgnoreCase));
        }

        if (captured is not null && IsCapturedPackageCurrent(package, captured))
        {
            return true;
        }

        diagnostics.Degrade(
            $"Skill '{skillId}' was removed because it is unavailable or changed since delegation.");
        return false;
    }

    private static bool IsCapturedPackageCurrent(
        ExtensionPackageRecord? package,
        DirectExtensionCapability captured)
        => package is not null &&
           package.IsEnabled &&
           ExtensionInstallation.IsIntact(package) &&
           string.Equals(package.Version, captured.Version, StringComparison.Ordinal) &&
           string.Equals(package.ContentHash, captured.ContentHash, StringComparison.Ordinal);

    private static bool Contains(IEnumerable<string> values, string value)
        => values.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static void ValidatePackageCapability(
        IReadOnlyList<ExtensionPackageRecord> packages,
        ExtensionKind kind,
        string id,
        IReadOnlyList<DirectExtensionCapability> ceiling)
    {
        var captured = ceiling.FirstOrDefault(capability =>
            string.Equals(capability.Id, id, StringComparison.OrdinalIgnoreCase));
        if (captured is null)
        {
            throw new InvalidDataException($"Capability '{id}' is not authorized by the captured ceiling.");
        }

        var package = packages.FirstOrDefault(item =>
            item.Kind == kind && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (package is null ||
            !package.IsEnabled ||
            !ExtensionInstallation.IsIntact(package) ||
            !string.Equals(package.Version, captured.Version, StringComparison.Ordinal) ||
            !string.Equals(package.ContentHash, captured.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Capability '{id}' is unavailable or changed after task acceptance.");
        }
    }

    private static void EnsureRequiredCapabilitiesResolved(
        DirectChatTurnRequest request,
        PluginCapabilities plugins,
        SkillCapabilities skills,
        McpCapabilities mcpCapabilities)
    {
        if (request.ExecutionContext.Origin != DirectTurnOrigin.Subagent)
        {
            return;
        }

        var missingPlugin = request.Agent.PluginIds.FirstOrDefault(id =>
            !plugins.PluginRoots.ContainsKey(id));
        if (missingPlugin is not null)
        {
            throw new InvalidDataException($"Required Plugin '{missingPlugin}' could not be loaded.");
        }

        var missingSkill = request.Agent.SkillIds.FirstOrDefault(id =>
            !skills.ResolvedSkillIds.Contains(id, StringComparer.OrdinalIgnoreCase));
        if (missingSkill is not null)
        {
            throw new InvalidDataException($"Required Skill '{missingSkill}' could not be loaded.");
        }

        var missingMcp = request.Agent.McpServerIds.FirstOrDefault(id =>
            !mcpCapabilities.Capabilities.Any(capability =>
                string.Equals(capability.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (missingMcp is not null)
        {
            throw new InvalidDataException($"Required MCP server '{missingMcp}' could not be loaded.");
        }
    }

    private static DirectCapabilityCeiling CreateEffectiveCeiling(
        DirectChatTurnRequest request,
        IReadOnlyList<ExtensionPackageRecord> packages,
        PluginCapabilities plugins,
        SkillCapabilities skills,
        McpCapabilities mcpCapabilities)
    {
        var pluginCapabilities = plugins.PluginRoots.Keys
            .Select(id => CreatePackageCapability(packages, ExtensionKind.Plugin, id))
            .Where(capability => capability is not null)
            .Cast<DirectExtensionCapability>()
            .ToArray();
        var skillCapabilities = skills.ResolvedSkillIds
            .Select(id => CreateSkillCapability(packages, id, pluginCapabilities))
            .Where(capability => capability is not null)
            .Cast<DirectExtensionCapability>()
            .ToArray();
        var capturedSubagents = request.ExecutionContext.CapabilityCeiling?.SubagentIds;
        var subagentIds = capturedSubagents is null
            ? request.Agent.SubagentIds.ToArray()
            : request.Agent.SubagentIds
                .Where(id => capturedSubagents.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        return new DirectCapabilityCeiling(
            request.ExecutionContext.CapabilityCeiling?.ToolPolicy ?? request.Agent.ToolPolicy,
            pluginCapabilities,
            skillCapabilities,
            mcpCapabilities.Capabilities,
            subagentIds);
    }

    private static DirectExtensionCapability? CreatePackageCapability(
        IReadOnlyList<ExtensionPackageRecord> packages,
        ExtensionKind kind,
        string id)
    {
        var package = packages.FirstOrDefault(item =>
            item.Kind == kind && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return package is null
            ? null
            : new DirectExtensionCapability(id, package.Version, package.ContentHash);
    }

    private static DirectExtensionCapability? CreateSkillCapability(
        IReadOnlyList<ExtensionPackageRecord> packages,
        string skillId,
        IReadOnlyList<DirectExtensionCapability> pluginCapabilities)
    {
        var standalone = CreatePackageCapability(packages, ExtensionKind.Skill, skillId);
        if (standalone is not null)
        {
            return standalone;
        }

        var separatorIndex = skillId.IndexOf('/');
        var pluginId = separatorIndex > 0 ? skillId[..separatorIndex] : string.Empty;
        var plugin = pluginCapabilities.FirstOrDefault(item =>
            string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        return plugin is null
            ? null
            : new DirectExtensionCapability(skillId, plugin.Version, plugin.ContentHash);
    }

    private static void ApplyExecutionToolPolicy(
        DirectChatTurnRequest request,
        List<AITool> tools,
        Dictionary<string, DirectToolDescriptor> descriptors)
    {
        if (request.ExecutionContext.Origin == DirectTurnOrigin.Interactive)
        {
            return;
        }

        var policy = request.Agent.ToolPolicy;
        var allowedNames = descriptors.Values
            .Where(descriptor => IsAllowedByPolicy(descriptor.Kind, policy))
            .Select(descriptor => descriptor.ProviderName)
            .ToHashSet(StringComparer.Ordinal);
        tools.RemoveAll(tool => !allowedNames.Contains(tool.Name));
        foreach (var name in descriptors.Keys.Where(name => !allowedNames.Contains(name)).ToArray())
        {
            descriptors.Remove(name);
        }
    }

    private static bool IsAllowedByPolicy(ToolCallKind kind, string policy)
        => policy switch
        {
            "none" => false,
            "read-only" => kind is ToolCallKind.List or ToolCallKind.Search or ToolCallKind.Read,
            AgentRuntimeDefinition.SystemToolPolicy => true,
            _ => false
        };

    private static int ToolPolicyRank(string policy)
        => policy switch
        {
            "none" => 0,
            "read-only" => 1,
            AgentRuntimeDefinition.SystemToolPolicy => 2,
            _ => int.MaxValue
        };
}
