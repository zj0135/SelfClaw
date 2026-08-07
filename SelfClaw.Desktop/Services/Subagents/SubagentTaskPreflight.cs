using System.IO;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.AiProviders.Abstractions;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskPreflight
{
    private readonly IAiProviderSettingsService _providerSettings;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IMcpServerRepository _mcpServerRepository;

    public SubagentTaskPreflight(
        IAiProviderSettingsService providerSettings,
        IExtensionPackageRepository packageRepository,
        IMcpServerRepository mcpServerRepository)
    {
        _providerSettings = providerSettings;
        _packageRepository = packageRepository;
        _mcpServerRepository = mcpServerRepository;
    }

    internal async Task<SubagentPreflightFailure?> CheckAsync(
        SubagentDefinitionSnapshot definition,
        SubagentTaskStartRequest request,
        Guid resolvedModelProfileId,
        CancellationToken cancellationToken)
    {
        if (!IsToolPolicyAuthorized(definition.ToolPolicy, request.CapabilityCeiling.ToolPolicy))
        {
            return NotAuthorized("The Subagent tool policy exceeds the parent capability ceiling.");
        }

        if (request.WorkspaceRoot is not null && !Directory.Exists(request.WorkspaceRoot.RootPath))
        {
            return new SubagentPreflightFailure(
                SubagentErrorCodes.WorkspaceUnavailable,
                "The captured workspace is no longer available.");
        }

        var enabledModels = await _providerSettings.ListEnabledModelsAsync(cancellationToken);
        if (!enabledModels.Any(model => model.ModelProfileId == resolvedModelProfileId))
        {
            return new SubagentPreflightFailure(
                SubagentErrorCodes.ModelUnavailable,
                "The selected Subagent model is unavailable.");
        }

        var packages = await _packageRepository.ListPackagesAsync(cancellationToken);
        var packageFailure = CheckPackages(definition, request.CapabilityCeiling, packages);
        if (packageFailure is not null)
        {
            return packageFailure;
        }

        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken);
        return CheckMcpServers(definition, request.CapabilityCeiling, servers);
    }

    private static SubagentPreflightFailure? CheckPackages(
        SubagentDefinitionSnapshot definition,
        DirectCapabilityCeiling ceiling,
        IReadOnlyList<ExtensionPackageRecord> packages)
    {
        foreach (var pluginId in definition.PluginIds)
        {
            var ceilingCapability = Find(ceiling.Plugins, pluginId);
            if (ceilingCapability is null)
            {
                return NotAuthorized($"Plugin '{pluginId}' is not authorized by the parent task.");
            }

            var package = packages.FirstOrDefault(item =>
                item.Kind == ExtensionKind.Plugin &&
                string.Equals(item.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (!IsCurrent(package, ceilingCapability, "plugin.json"))
            {
                return Unavailable($"Plugin '{pluginId}' is unavailable or changed after task acceptance.");
            }
        }

        foreach (var skillId in definition.SkillIds)
        {
            var ceilingCapability = Find(ceiling.Skills, skillId);
            if (ceilingCapability is null)
            {
                return NotAuthorized($"Skill '{skillId}' is not authorized by the parent task.");
            }

            var package = packages.FirstOrDefault(item =>
                item.Kind == ExtensionKind.Skill &&
                string.Equals(item.Id, skillId, StringComparison.OrdinalIgnoreCase));
            if (package is not null)
            {
                if (!IsCurrent(package, ceilingCapability, "SKILL.md"))
                {
                    return Unavailable($"Skill '{skillId}' is unavailable or changed after task acceptance.");
                }

                continue;
            }

            var separatorIndex = skillId.IndexOf('/');
            var pluginId = separatorIndex > 0 ? skillId[..separatorIndex] : string.Empty;
            if (pluginId.Length == 0 || Find(ceiling.Plugins, pluginId) is null)
            {
                return Unavailable($"Skill '{skillId}' is unavailable.");
            }
        }

        return null;
    }

    private static SubagentPreflightFailure? CheckMcpServers(
        SubagentDefinitionSnapshot definition,
        DirectCapabilityCeiling ceiling,
        IReadOnlyList<McpServerConfigRecord> servers)
    {
        foreach (var serverId in definition.McpServerIds)
        {
            var ceilingCapability = ceiling.McpServers.FirstOrDefault(item =>
                string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (ceilingCapability is null)
            {
                return NotAuthorized($"MCP server '{serverId}' is not authorized by the parent task.");
            }

            var server = servers.FirstOrDefault(item =>
                string.Equals(item.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (server is null || !server.IsEnabled || server.ConfigRevision != ceilingCapability.ConfigRevision)
            {
                return Unavailable($"MCP server '{serverId}' is unavailable or changed after task acceptance.");
            }
        }

        return null;
    }

    private static bool IsCurrent(
        ExtensionPackageRecord? package,
        DirectExtensionCapability ceiling,
        string manifestName)
    {
        if (package is null)
        {
            return false;
        }

        return package.IsEnabled &&
               string.Equals(package.Version, ceiling.Version, StringComparison.Ordinal) &&
               string.Equals(package.ContentHash, ceiling.ContentHash, StringComparison.Ordinal) &&
               Directory.Exists(package.InstallPath) &&
               File.Exists(Path.Combine(package.InstallPath, manifestName));
    }

    private static DirectExtensionCapability? Find(
        IReadOnlyList<DirectExtensionCapability> capabilities,
        string id)
        => capabilities.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsToolPolicyAuthorized(string requested, string ceiling)
        => ToolPolicyRank(requested) <= ToolPolicyRank(ceiling);

    private static int ToolPolicyRank(string policy)
        => policy switch
        {
            "none" => 0,
            "read-only" => 1,
            AgentRuntimeDefinition.SystemToolPolicy => 2,
            _ => int.MaxValue
        };

    private static SubagentPreflightFailure NotAuthorized(string message)
        => new(SubagentErrorCodes.CapabilityNotAuthorized, message);

    private static SubagentPreflightFailure Unavailable(string message)
        => new(SubagentErrorCodes.CapabilityUnavailable, message);
}
