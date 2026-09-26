using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

internal static class DirectCapabilityRules
{
    internal static bool IsToolPolicyAuthorized(string requested, string ceiling)
    {
        var rank = ToolPolicyRank(requested);
        return rank >= 0 && rank <= ToolPolicyRank(ceiling);
    }

    internal static string RestrictToolPolicy(string requested, string ceiling)
        => IsToolPolicyAuthorized(requested, ceiling) ? requested : ceiling;

    internal static bool Allows(ToolCallKind kind, string policy)
        => policy switch
        {
            "none" => false,
            "read-only" => kind is ToolCallKind.List or ToolCallKind.Search or ToolCallKind.Read,
            AgentRuntimeDefinition.SystemToolPolicy => true,
            _ => throw new InvalidDataException($"Unknown Direct tool policy '{policy}'.")
        };

    internal static SubagentPreflightFailure? CheckPackages(
        string toolPolicy, IReadOnlyList<string> pluginIds, IReadOnlyList<string> skillIds,
        DirectCapabilityCeiling ceiling, IReadOnlyList<ExtensionPackageRecord> packages)
    {
        if (!IsToolPolicyAuthorized(toolPolicy, ceiling.ToolPolicy))
        {
            return NotAuthorized("The requested tool policy exceeds the captured capability ceiling.");
        }

        foreach (var id in pluginIds)
        {
            var captured = Find(ceiling.Plugins, id);
            if (captured is null) return NotAuthorized($"Plugin '{id}' is not authorized by the captured ceiling.");
            if (!IsPackageCurrent(FindPackage(packages, ExtensionKind.Plugin, id), captured))
                return Unavailable($"Plugin '{id}' is unavailable or changed after task acceptance.");
        }

        foreach (var id in skillIds)
        {
            if (Find(ceiling.Skills, id) is null) return NotAuthorized($"Skill '{id}' is not authorized by the captured ceiling.");
            if (!IsSkillCurrent(id, pluginIds, ceiling, packages))
                return Unavailable($"Skill '{id}' is unavailable or changed after task acceptance.");
        }

        // Hook Plugins inherited from the parent turn are part of the delegated policy: a changed one
        // must fail the task before acceptance rather than let the child run without its hooks.
        foreach (var hookPlugin in ceiling.HookPlugins ?? [])
        {
            if (!IsPackageCurrent(FindPackage(packages, ExtensionKind.Plugin, hookPlugin.Id), hookPlugin))
                return Unavailable($"Plugin '{hookPlugin.Id}' is unavailable or changed after task acceptance.");
        }

        return null;
    }

    internal static bool IsSkillCurrent(string id, IReadOnlyList<string> pluginIds,
        DirectCapabilityCeiling ceiling, IReadOnlyList<ExtensionPackageRecord> packages)
    {
        var captured = Find(ceiling.Skills, id);
        if (captured is null) return false;
        var standalone = FindPackage(packages, ExtensionKind.Skill, id);
        if (standalone is not null) return IsPackageCurrent(standalone, captured);
        var separator = id.IndexOf('/');
        if (separator <= 0) return false;
        var pluginId = id[..separator];
        var plugin = FindPackage(packages, ExtensionKind.Plugin, pluginId);
        return pluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase) &&
               IsPackageCurrent(plugin, Find(ceiling.Plugins, pluginId)) &&
               IsPackageCurrent(plugin, captured);
    }

    internal static bool IsPackageCurrent(ExtensionPackageRecord? package, DirectExtensionCapability? captured)
        => package is not null && captured is not null && package.IsEnabled && ExtensionInstallation.IsIntact(package) &&
           string.Equals(package.Version, captured.Version, StringComparison.Ordinal) &&
           string.Equals(package.ContentHash, captured.ContentHash, StringComparison.Ordinal);

    internal static bool IsMcpCurrent(McpServerConfigRecord? server, DirectCapabilityCeiling ceiling)
        => server is { IsEnabled: true } && ceiling.McpServers.Any(captured =>
            string.Equals(captured.Id, server.Id, StringComparison.OrdinalIgnoreCase) && captured.ConfigRevision == server.ConfigRevision);

    internal static SubagentPreflightFailure? CheckMcpServers(IReadOnlyList<string> serverIds,
        DirectCapabilityCeiling ceiling, IReadOnlyList<McpServerConfigRecord> servers)
    {
        foreach (var id in serverIds)
        {
            if (!ceiling.McpServers.Any(captured => string.Equals(captured.Id, id, StringComparison.OrdinalIgnoreCase)))
                return NotAuthorized($"MCP server '{id}' is not authorized by the captured ceiling.");
            var server = servers.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (!IsMcpCurrent(server, ceiling))
                return Unavailable($"MCP server '{id}' is unavailable or changed after task acceptance.");
        }

        return null;
    }

    private static ExtensionPackageRecord? FindPackage(IReadOnlyList<ExtensionPackageRecord> packages, ExtensionKind kind, string id)
        => packages.FirstOrDefault(package => package.Kind == kind && string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase));

    private static DirectExtensionCapability? Find(IReadOnlyList<DirectExtensionCapability> capabilities, string id)
        => capabilities.FirstOrDefault(capability => string.Equals(capability.Id, id, StringComparison.OrdinalIgnoreCase));

    private static int ToolPolicyRank(string policy)
        => policy switch { "none" => 0, "read-only" => 1, AgentRuntimeDefinition.SystemToolPolicy => 2, _ => -1 };

    private static SubagentPreflightFailure NotAuthorized(string message) => new(SubagentErrorCodes.CapabilityNotAuthorized, message);
    private static SubagentPreflightFailure Unavailable(string message) => new(SubagentErrorCodes.CapabilityUnavailable, message);
}
