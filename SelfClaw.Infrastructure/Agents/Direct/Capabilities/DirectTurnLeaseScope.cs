using SelfClaw.Infrastructure.Extensions.Mcp;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// The single owner of every lease acquired while assembling one turn's capabilities. Plugin version
/// leases and MCP client leases are handed to this scope as they are taken; it disposes them exactly
/// once - through the turn's <see cref="DirectTurnCapabilityLease"/>, or immediately when resolution
/// fails before that lease exists. Sources never dispose a lease the scope already accepted.
/// </summary>
internal sealed class DirectTurnLeaseScope
{
    private readonly List<IDisposable> _pluginLeases = [];
    private readonly List<McpClientLease> _mcpLeases = [];
    private readonly object _sync = new();
    private int _disposed;

    /// <summary>
    /// Hands a lease to the scope. Returns <c>false</c> when the scope is already disposed (resolution
    /// failed concurrently), telling the caller to dispose the lease itself.
    /// </summary>
    public bool Add(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _pluginLeases.Add(lease);
            return true;
        }
    }

    public bool Add(McpClientLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _mcpLeases.Add(lease);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<McpClientLease> mcpLeases;
        List<IDisposable> pluginLeases;
        lock (_sync)
        {
            mcpLeases = [.. _mcpLeases];
            pluginLeases = [.. _pluginLeases];
        }

        try
        {
            for (var index = mcpLeases.Count - 1; index >= 0; index--)
            {
                await mcpLeases[index].DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var lease in pluginLeases.AsEnumerable().Reverse())
            {
                lease.Dispose();
            }
        }
    }
}
