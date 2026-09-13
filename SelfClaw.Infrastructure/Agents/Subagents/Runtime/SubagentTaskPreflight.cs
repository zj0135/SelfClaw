using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal sealed class SubagentTaskPreflight : ISubagentTaskPreflight
{
    private readonly IAiModelCatalog _models;
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IMcpServerRepository _mcpServerRepository;

    public SubagentTaskPreflight(IAiModelCatalog models, IExtensionPackageRepository packageRepository,
        IMcpServerRepository mcpServerRepository)
    {
        _models = models;
        _packageRepository = packageRepository;
        _mcpServerRepository = mcpServerRepository;
    }

    public async Task<SubagentPreflightFailure?> CheckAsync(SubagentDefinitionSnapshot definition,
        SubagentTaskStartRequest request, Guid resolvedModelProfileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        if (!DirectCapabilityRules.IsToolPolicyAuthorized(definition.ToolPolicy, request.CapabilityCeiling.ToolPolicy))
            return new(SubagentErrorCodes.CapabilityNotAuthorized, "The Subagent tool policy exceeds the parent capability ceiling.");
        if (request.WorkspaceRoot is not null && !Directory.Exists(request.WorkspaceRoot.RootPath))
            return new(SubagentErrorCodes.WorkspaceUnavailable, "The captured workspace is no longer available.");
        if (!await _models.IsModelAvailableAsync(resolvedModelProfileId, cancellationToken).ConfigureAwait(false))
            return new(SubagentErrorCodes.ModelUnavailable, "The selected Subagent model is unavailable.");

        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var packageFailure = DirectCapabilityRules.CheckPackages(definition.ToolPolicy, definition.PluginIds,
            definition.SkillIds, request.CapabilityCeiling, packages);
        if (packageFailure is not null) return packageFailure;
        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        return DirectCapabilityRules.CheckMcpServers(definition.McpServerIds, request.CapabilityCeiling, servers);
    }
}
