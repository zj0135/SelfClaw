using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// Connects the Agent's bound MCP servers for one turn and adds their tools to the turn's tool set.
/// A single server that cannot be configured or connected degrades this turn; every lease the source
/// acquires is handed to the turn's <see cref="DirectTurnLeaseScope"/>, which owns its lifetime.
/// </summary>
internal sealed class McpCapabilitySource
{
    /// <summary>Independent servers connect concurrently, bounded so a turn cannot stampede processes.</summary>
    internal const int MaximumConcurrentServers = 4;

    private readonly IMcpServerRepository _serverRepository;
    private readonly McpConfigurationResolver _configurationResolver;
    private readonly IMcpClientManager _clientManager;
    private readonly McpToolAdapter _toolAdapter;
    private readonly IExtensionStateChangeNotifier _stateChangeNotifier;

    public McpCapabilitySource(
        IMcpServerRepository serverRepository,
        McpConfigurationResolver configurationResolver,
        IMcpClientManager clientManager,
        McpToolAdapter toolAdapter,
        IExtensionStateChangeNotifier stateChangeNotifier)
    {
        _serverRepository = serverRepository;
        _configurationResolver = configurationResolver;
        _clientManager = clientManager;
        _toolAdapter = toolAdapter;
        _stateChangeNotifier = stateChangeNotifier;
    }

    public async Task<McpCapabilities> AddToolsAsync(
        DirectChatTurnRequest request,
        ICollection<AITool> tools,
        IDictionary<string, DirectToolDescriptor> descriptors,
        DirectTurnLeaseScope leases,
        TurnDiagnostics diagnostics,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        CancellationToken cancellationToken)
    {
        if (request.Agent.McpServerIds.Count == 0 && effectivePluginRoots.Count == 0)
        {
            return new McpCapabilities([]);
        }

        var servers = await _serverRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        var effectiveServers = servers
            .Where(server => server.IsEnabled &&
                             (string.IsNullOrWhiteSpace(server.SourcePluginId)
                                 ? request.Agent.McpServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)
                                 : effectivePluginRoots.ContainsKey(server.SourcePluginId)) &&
                             IsAllowedByCapturedCeiling(request, server, diagnostics))
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();

        var resolutions = await ResolveServersAsync(
                effectiveServers,
                request,
                effectivePluginRoots,
                leases,
                cancellationToken)
            .ConfigureAwait(false);
        MergeResolutions(resolutions, tools, descriptors, diagnostics, _stateChangeNotifier);
        return new McpCapabilities(resolutions
            .Where(resolution => resolution.Lease is not null)
            .Select(resolution => new DirectMcpCapability(resolution.Server.Id, resolution.Server.ConfigRevision))
            .ToArray());
    }

    public static async ValueTask DisposeLeasesAsync(IReadOnlyList<McpClientLease> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsAllowedByCapturedCeiling(
        DirectChatTurnRequest request,
        McpServerConfigRecord server,
        TurnDiagnostics diagnostics)
    {
        if (request.ExecutionContext.Origin == DirectTurnOrigin.Interactive)
        {
            return true;
        }

        var captured = request.ExecutionContext.CapabilityCeiling?.McpServers.FirstOrDefault(capability =>
            string.Equals(capability.Id, server.Id, StringComparison.OrdinalIgnoreCase));
        if (captured is not null && captured.ConfigRevision == server.ConfigRevision)
        {
            return true;
        }

        if (request.ExecutionContext.Origin == DirectTurnOrigin.Continuation)
        {
            diagnostics.Degrade(
                $"MCP server '{server.Id}' was removed because it changed since delegation.");
        }

        return false;
    }

    /// <summary>
    /// Resolves, connects to, and discovers the tools of the independent servers concurrently within a
    /// bounded degree of parallelism. Each slot reports into its own buffer so the merge stays
    /// deterministic; a lease is owned by the lease scope from the moment it exists, so cancellation
    /// or failure disposes every completed connection exactly once.
    /// </summary>
    private async Task<McpServerResolution[]> ResolveServersAsync(
        IReadOnlyList<McpServerConfigRecord> servers,
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        DirectTurnLeaseScope leases,
        CancellationToken cancellationToken)
    {
        var resolutions = new McpServerResolution[servers.Count];
        await Parallel.ForEachAsync(
                servers.Select((server, index) => (server, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaximumConcurrentServers,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    resolutions[item.index] = await ResolveServerAsync(
                            item.server,
                            request,
                            effectivePluginRoots,
                            leases,
                            token)
                        .ConfigureAwait(false);
                })
            .ConfigureAwait(false);
        return resolutions;
    }

    /// <summary>
    /// Applies the buffered per-server results in server order. State change notifications also fire
    /// here - serially, so subscribers observe the same event order they got before the connects ran
    /// concurrently.
    /// </summary>
    private static void MergeResolutions(
        McpServerResolution[] resolutions,
        ICollection<AITool> tools,
        IDictionary<string, DirectToolDescriptor> descriptors,
        TurnDiagnostics diagnostics,
        IExtensionStateChangeNotifier stateChangeNotifier)
    {
        foreach (var resolution in resolutions)
        {
            foreach (var message in resolution.Messages)
            {
                diagnostics.Info(message);
            }

            foreach (var degradation in resolution.Degradations)
            {
                diagnostics.Degrade(degradation);
            }

            foreach (var tool in resolution.Tools)
            {
                tools.Add(tool);
            }

            foreach (var descriptor in resolution.Descriptors)
            {
                if (!descriptors.TryAdd(descriptor.ProviderName, descriptor))
                {
                    throw new InvalidDataException(
                        $"MCP tool name collision for '{descriptor.ProviderName}'.");
                }
            }

            if (resolution.HealthRecorded)
            {
                stateChangeNotifier.Advance();
            }
        }
    }

    /// <summary>
    /// Returns one server's resolution - with a lease when the server connected, or without one when it
    /// degraded out of the turn. An exception after the lease was acquired propagates with the lease
    /// owned by the lease scope.
    /// </summary>
    private async Task<McpServerResolution> ResolveServerAsync(
        McpServerConfigRecord server,
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        DirectTurnLeaseScope leases,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var degradations = new List<string>();
        var configuration = await _configurationResolver.ResolveAsync(
                server,
                request.WorkspaceRoot?.RootPath,
                string.IsNullOrWhiteSpace(server.SourcePluginId)
                    ? null
                    : effectivePluginRoots.GetValueOrDefault(server.SourcePluginId),
                cancellationToken)
            .ConfigureAwait(false);
        if (!configuration.IsAvailable)
        {
            degradations.Add($"MCP server '{server.Id}' was skipped: {configuration.UnavailableReason}");
            var configHealthRecorded = await TryRecordHealthAsync(
                    server,
                    McpServerHealthStatus.NeedsConfiguration,
                    configuration.UnavailableReason,
                    [],
                    messages,
                    cancellationToken)
                .ConfigureAwait(false);
            return new McpServerResolution(server, null, messages, degradations, healthRecorded: configHealthRecorded);
        }

        McpClientLease lease;
        try
        {
            lease = await _clientManager.AcquireAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The connection failure text can carry endpoint and credential detail, so only a fixed
            // message reaches the model and the settings page.
            const string failure = "Connection or tool discovery failed.";
            degradations.Add($"MCP server '{server.Id}' was skipped: {failure}");
            var failureHealthRecorded = await TryRecordHealthAsync(
                    server,
                    McpServerHealthStatus.Degraded,
                    failure,
                    [],
                    messages,
                    cancellationToken)
                .ConfigureAwait(false);
            return new McpServerResolution(server, null, messages, degradations, healthRecorded: failureHealthRecorded);
        }

        // The scope owns the lease from here on: a later failure propagates and the scope's dispose
        // releases this connection together with the rest of the turn's leases.
        if (!leases.Add(lease))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException("Capability resolution was already torn down.");
        }

        var healthRecorded = await TryRecordHealthAsync(
                server,
                McpServerHealthStatus.Ready,
                null,
                lease.Tools.Select(tool => tool.Name).ToArray(),
                messages,
                cancellationToken)
            .ConfigureAwait(false);
        var tools = new List<AITool>();
        var toolDescriptors = new List<DirectToolDescriptor>();
        foreach (var mcpTool in lease.Tools)
        {
            var (Tool, Descriptor) = _toolAdapter.Create(
                mcpTool,
                configuration,
                request.ConversationId,
                request.ToolPermissionMode,
                request.ToolApprovalHandler);
            tools.Add(Tool);
            toolDescriptors.Add(Descriptor);
        }

        return new McpServerResolution(
            server,
            lease,
            messages,
            degradations,
            healthRecorded: healthRecorded,
            tools,
            toolDescriptors);
    }

    private async Task<bool> TryRecordHealthAsync(
        McpServerConfigRecord server,
        McpServerHealthStatus status,
        string? error,
        IReadOnlyList<string> tools,
        ICollection<string> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            // The observation is conditioned on the configuration revision it was taken against: a
            // concurrent enable/disable or settings change makes it stale, and a stale observation
            // must never overwrite that change. The caller notifies subscribers only for recorded
            // observations, so the event order stays deterministic.
            return await _serverRepository.UpdateMcpServerHealthAsync(
                    server.Id,
                    server.ConfigRevision,
                    status,
                    error,
                    tools,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Health is an observation about the turn, not part of it: failing to persist it must not
            // take down a server that actually connected.
            messages.Add($"MCP server '{server.Id}' health could not be persisted.");
            return false;
        }
    }

    /// <summary>One server's buffered result, merged in server order after all servers resolved.</summary>
    private sealed class McpServerResolution(
        McpServerConfigRecord server,
        McpClientLease? lease,
        List<string> messages,
        List<string> degradations,
        bool healthRecorded = false,
        List<AITool>? tools = null,
        List<DirectToolDescriptor>? descriptors = null)
    {
        public McpServerConfigRecord Server { get; } = server;
        public McpClientLease? Lease { get; } = lease;
        public bool HealthRecorded { get; } = healthRecorded;
        public List<AITool> Tools { get; } = tools ?? [];
        public List<DirectToolDescriptor> Descriptors { get; } = descriptors ?? [];
        public List<string> Messages { get; } = messages;
        public List<string> Degradations { get; } = degradations;
    }
}
