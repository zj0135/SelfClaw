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
/// A single server that cannot be configured or connected degrades this turn; the returned leases keep
/// the connections alive until the capability lease is disposed.
/// </summary>
internal sealed class McpCapabilitySource
{
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

    public async Task<IReadOnlyList<McpClientLease>> AddToolsAsync(
        DirectChatTurnRequest request,
        ICollection<AITool> tools,
        IDictionary<string, DirectToolDescriptor> descriptors,
        TurnDiagnostics diagnostics,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        CancellationToken cancellationToken)
    {
        if (request.Agent.McpServerIds.Count == 0 && effectivePluginRoots.Count == 0)
        {
            return [];
        }

        var servers = await _serverRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        var effectiveServers = servers
            .Where(server => server.IsEnabled &&
                             (string.IsNullOrWhiteSpace(server.SourcePluginId)
                                 ? request.Agent.McpServerIds.Contains(server.Id, StringComparer.OrdinalIgnoreCase)
                                 : effectivePluginRoots.ContainsKey(server.SourcePluginId)))
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .ToArray();
        var leases = new List<McpClientLease>();
        try
        {
            foreach (var server in effectiveServers)
            {
                var lease = await AddServerToolsAsync(
                        server,
                        request,
                        tools,
                        descriptors,
                        diagnostics,
                        effectivePluginRoots,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (lease is not null)
                {
                    leases.Add(lease);
                }
            }

            return leases;
        }
        catch
        {
            await DisposeLeasesAsync(leases).ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask DisposeLeasesAsync(IReadOnlyList<McpClientLease> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the acquired lease, or <c>null</c> when this server degraded out of the turn. The lease is
    /// added to the caller's list before any tool is registered so a name collision still releases it.
    /// </summary>
    private async Task<McpClientLease?> AddServerToolsAsync(
        McpServerConfigRecord server,
        DirectChatTurnRequest request,
        ICollection<AITool> tools,
        IDictionary<string, DirectToolDescriptor> descriptors,
        TurnDiagnostics diagnostics,
        IReadOnlyDictionary<string, string> effectivePluginRoots,
        CancellationToken cancellationToken)
    {
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
            diagnostics.Degrade($"MCP server '{server.Id}' was skipped: {configuration.UnavailableReason}");
            await TryRecordHealthAsync(
                    server,
                    McpServerHealthStatus.NeedsConfiguration,
                    configuration.UnavailableReason,
                    [],
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
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
            diagnostics.Degrade($"MCP server '{server.Id}' was skipped: {failure}");
            await TryRecordHealthAsync(
                    server,
                    McpServerHealthStatus.Degraded,
                    failure,
                    [],
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            await TryRecordHealthAsync(
                    server,
                    McpServerHealthStatus.Ready,
                    null,
                    lease.Tools.Select(tool => tool.Name).ToArray(),
                    diagnostics,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var mcpTool in lease.Tools)
            {
                var (Tool, Descriptor) = _toolAdapter.Create(
                    mcpTool,
                    configuration,
                    request.ConversationId,
                    request.ToolPermissionMode,
                    request.ToolApprovalHandler);
                if (!descriptors.TryAdd(Descriptor.ProviderName, Descriptor))
                {
                    throw new InvalidDataException(
                        $"MCP tool name collision for '{Descriptor.ProviderName}'.");
                }

                tools.Add(Tool);
            }

            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryRecordHealthAsync(
        McpServerConfigRecord server,
        McpServerHealthStatus status,
        string? error,
        IReadOnlyList<string> tools,
        TurnDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _serverRepository.UpsertMcpServerAsync(
                    server with
                    {
                        DiscoveredTools = tools,
                        LastStatus = status,
                        LastError = error,
                        LastCheckedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            _stateChangeNotifier.Advance();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Health is an observation about the turn, not part of it: failing to persist it must not
            // take down a server that actually connected.
            diagnostics.Info($"MCP server '{server.Id}' health could not be persisted.");
        }
    }
}
