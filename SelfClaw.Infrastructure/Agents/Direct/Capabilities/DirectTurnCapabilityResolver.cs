using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// The only seam between the Direct runtime and the extension system: given a turn request, it returns an
/// immutable, disposable snapshot of the capabilities that turn may use. It owns the two-layer switch
/// (installed and globally enabled ∩ bound to this Agent) and delegates each capability kind to its own
/// source.
/// </summary>
internal sealed class DirectTurnCapabilityResolver : IDirectTurnCapabilityResolver
{
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
        var bindings = effectiveRequest.WorkspaceRoot is null
            ? new List<DirectToolBinding>()
            : _workspaceToolset.CreateTools(effectiveRequest.WorkspaceRoot).ToList();
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
                InheritedHookPlugins(effectiveRequest),
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
        bindings.AddRange(skills.Tools);

        var systemInstructions = new List<string>();
        systemInstructions.AddRange(plugins.Instructions);
        systemInstructions.AddRange(skills.Instructions);
        var mcpCapabilities = await _mcpSource.ResolveAsync(
                effectiveRequest,
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
        bindings.AddRange(mcpCapabilities.Tools);
        bindings.AddRange(_subagentSource.CreateTools(effectiveRequest, effectiveCeiling));
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
            BindTools(effectiveRequest, bindings),
            skills.MessageAdjustments,
            diagnostics.Messages,
            leases.DisposeAsync,
            plugins.Hooks,
            plugins.HookNotices,
            plugins.HookBlockReason);
    }

    private static IReadOnlyList<DirectExtensionCapability> InheritedHookPlugins(DirectChatTurnRequest request)
        => request.ExecutionContext.Origin is DirectTurnOrigin.Subagent or DirectTurnOrigin.Continuation
            ? request.ExecutionContext.CapabilityCeiling?.HookPlugins ?? []
            : [];

    internal static IReadOnlyList<DirectToolBinding> BindTools(
        DirectChatTurnRequest request, IEnumerable<DirectToolBinding> bindings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DirectToolBinding>();
        foreach (var binding in bindings)
        {
            if (!names.Add(binding.Tool.Name) || binding.Tool.Name != binding.Descriptor.ProviderName)
            {
                throw new InvalidDataException($"Direct tool name collision or descriptor mismatch for '{binding.Tool.Name}'.");
            }

            if (!DirectCapabilityRules.Allows(binding.Descriptor.Kind, request.Agent.ToolPolicy)) continue;
            result.Add(binding);
        }

        return result;
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
        var failure = DirectCapabilityRules.CheckPackages(request.Agent.ToolPolicy, request.Agent.PluginIds,
            request.Agent.SkillIds, ceiling, packages);
        if (failure is not null)
        {
            throw new InvalidDataException(failure.ErrorMessage);
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
                pluginIds,
                ceiling,
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
        var toolPolicy = DirectCapabilityRules.RestrictToolPolicy(request.Agent.ToolPolicy, ceiling.ToolPolicy);
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
            if (DirectCapabilityRules.IsPackageCurrent(package, captured))
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
        IReadOnlyList<string> pluginIds,
        DirectCapabilityCeiling ceiling,
        IReadOnlyList<ExtensionPackageRecord> packages,
        TurnDiagnostics diagnostics)
    {
        if (DirectCapabilityRules.IsSkillCurrent(skillId, pluginIds, ceiling, packages))
        {
            return true;
        }

        diagnostics.Degrade(
            $"Skill '{skillId}' was removed because it is unavailable or changed since delegation.");
        return false;
    }

    private static bool Contains(IEnumerable<string> values, string value)
        => values.Contains(value, StringComparer.OrdinalIgnoreCase);

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
        // Only Plugins whose hooks actually resolved survive into the next delegation: they are the
        // policy an inherited turn must keep enforcing.
        var hookPlugins = plugins.Hooks
            .Select(hook => hook.PluginId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => CreatePackageCapability(packages, ExtensionKind.Plugin, id))
            .Where(capability => capability is not null)
            .Cast<DirectExtensionCapability>()
            .ToArray();
        return new DirectCapabilityCeiling(
            request.Agent.ToolPolicy,
            pluginCapabilities,
            skillCapabilities,
            mcpCapabilities.Capabilities,
            subagentIds,
            hookPlugins);
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

}
