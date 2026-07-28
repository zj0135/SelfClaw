using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
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

    public DirectTurnCapabilityResolver(
        WorkspaceAgentToolset workspaceToolset,
        IExtensionPackageRepository packageRepository,
        SkillCapabilitySource skillSource,
        PluginCapabilitySource pluginSource,
        McpCapabilitySource mcpSource)
    {
        _workspaceToolset = workspaceToolset;
        _packageRepository = packageRepository;
        _skillSource = skillSource;
        _pluginSource = pluginSource;
        _mcpSource = mcpSource;
    }

    public async Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pluginLeases = new List<PluginVersionLease>();
        try
        {
            return await ResolveCoreAsync(request, pluginLeases, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposePluginLeasesAsync(pluginLeases).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<DirectTurnCapabilityLease> ResolveCoreAsync(
        DirectChatTurnRequest request,
        ICollection<PluginVersionLease> pluginLeases,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new TurnDiagnostics();
        var (tools, descriptors) = CreateWorkspaceCapabilities(request, diagnostics);
        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var installedSkills = packages
            .Where(package => package.Kind == ExtensionKind.Skill)
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var effectiveSkills = installedSkills.Values
            .Where(package => package.IsEnabled &&
                              ExtensionInstallation.IsIntact(package) &&
                              request.Agent.SkillIds.Contains(package.Id, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var plugins = await _pluginSource.ResolveAsync(
                request.Agent,
                packages,
                effectiveSkills,
                diagnostics,
                pluginLeases,
                cancellationToken)
            .ConfigureAwait(false);
        var skills = await _skillSource.ResolveAsync(
                request,
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
        var mcpLeases = await _mcpSource.AddToolsAsync(
                request,
                tools,
                descriptors,
                diagnostics,
                plugins.PluginRoots,
                cancellationToken)
            .ConfigureAwait(false);
        if (diagnostics.Degradations.Count > 0)
        {
            systemInstructions.Add(CapabilitySections.Degradation(diagnostics.Degradations));
        }

        // The policy only earns its tokens once something extension-provided is actually in play.
        if (systemInstructions.Count > 0 || mcpLeases.Count > 0)
        {
            systemInstructions.Insert(0, CapabilitySections.Policy);
        }

        return new DirectTurnCapabilityLease(
            systemInstructions,
            tools,
            descriptors,
            skills.MessageAdjustments,
            diagnostics.Messages,
            () => DisposeCapabilityLeasesAsync(mcpLeases, pluginLeases));
    }

    private (List<AITool> Tools, Dictionary<string, DirectToolDescriptor> Descriptors) CreateWorkspaceCapabilities(
        DirectChatTurnRequest request,
        TurnDiagnostics diagnostics)
    {
        // v1 accepts only the "system" policy; anything else is reported and treated as "system" rather
        // than silently narrowing the tool set.
        if (!string.Equals(
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

    private static async ValueTask DisposePluginLeasesAsync(IEnumerable<PluginVersionLease> leases)
    {
        foreach (var lease in leases.Reverse())
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeCapabilityLeasesAsync(
        IReadOnlyList<McpClientLease> mcpLeases,
        IEnumerable<PluginVersionLease> pluginLeases)
    {
        try
        {
            await McpCapabilitySource.DisposeLeasesAsync(mcpLeases).ConfigureAwait(false);
        }
        finally
        {
            await DisposePluginLeasesAsync(pluginLeases).ConfigureAwait(false);
        }
    }
}
