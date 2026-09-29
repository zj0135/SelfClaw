using System.Runtime.ExceptionServices;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// The single owner of every lease acquired while assembling one turn's capabilities. Plugin version leases
/// and MCP client leases are handed to this scope as they are taken and released in reverse acquisition
/// order, so the connections that consumed a Plugin's configuration are closed before its version lease is
/// released. It disposes them exactly once - through the turn's
/// <see cref="DirectTurnCapabilityLease"/>, or immediately when resolution fails before that lease exists.
/// Sources never dispose a lease the scope already accepted.
/// </summary>
internal sealed class DirectTurnLeaseScope
{
    private readonly List<IAsyncDisposable> _leases = [];
    private readonly object _sync = new();
    private int _disposed;

    /// <summary>Hands an already-created lease to the scope.</summary>
    public bool Add(IAsyncDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _leases.Add(lease);
            return true;
        }
    }

    /// <summary>Hands a Plugin version lease, whose release is synchronous, to the scope.</summary>
    public bool Add(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Add(new SynchronousLease(lease));
    }

    /// <summary>
    /// Returns <c>false</c> when the scope is already disposed (resolution failed concurrently), telling the
    /// caller to dispose the lease itself.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IAsyncDisposable[] leases;
        lock (_sync)
        {
            leases = [.. _leases];
        }

        // Every accepted lease is released even when one of them throws, so a single broken Plugin or MCP
        // connection cannot leak the rest of the turn's leases. The first failure is rethrown.
        Exception? failure = null;
        for (var index = leases.Length - 1; index >= 0; index--)
        {
            try
            {
                await leases[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class SynchronousLease(IDisposable lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
