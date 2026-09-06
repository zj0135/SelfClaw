using System.Diagnostics;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpClientManager : IMcpClientManager, IAsyncDisposable
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(25);

    private readonly IMcpClientConnectionFactory _connectionFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private int _disposed;

    public McpClientManager(IMcpClientConnectionFactory connectionFactory)
        : this(connectionFactory, DefaultIdleTimeout)
    {
    }

    internal McpClientManager(IMcpClientConnectionFactory connectionFactory, TimeSpan idleTimeout)
    {
        _connectionFactory = connectionFactory;
        _idleTimeout = idleTimeout;
    }

    public async Task<McpClientLease> AcquireAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfDisposed();
        if (!configuration.IsAvailable)
        {
            throw new InvalidOperationException(configuration.UnavailableReason ?? "MCP server is unavailable.");
        }

        PoolEntry entry;
        List<PoolEntry> entriesToDispose;
        lock (_sync)
        {
            ThrowIfDisposed();
            var key = CreatePoolKey(configuration);
            entriesToDispose = MarkOlderEntriesDraining(configuration.Id, configuration.ConfigRevision);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new PoolEntry(
                    key,
                    configuration.Id,
                    configuration.ConfigRevision,
                    ConnectAsync(configuration),
                    DisposeIdleEntryAsync);
                _entries.Add(key, entry);
            }

            entry.Acquire();
        }

        foreach (var staleEntry in entriesToDispose)
        {
            await DisposeEntryAsync(staleEntry).ConfigureAwait(false);
        }

        try
        {
            var connection = await entry.ConnectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new McpClientLease(connection.Tools, () => ReleaseAsync(entry));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseCanceledAcquire(entry);
            throw;
        }
        catch
        {
            await ReleaseFailedAcquireAsync(entry).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<McpHealthResult> TestAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsAvailable)
        {
            return new McpHealthResult(
                configuration.Id,
                McpServerHealthStatus.NeedsConfiguration,
                null,
                configuration.UnavailableReason,
                []);
        }

        var stopwatch = Stopwatch.StartNew();
        using var testTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        testTimeout.CancelAfter(TestTimeout);
        try
        {
            await using var connection = await _connectionFactory.ConnectAsync(configuration, testTimeout.Token)
                .ConfigureAwait(false);
            await connection.PingAsync(testTimeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new McpHealthResult(
                configuration.Id,
                McpServerHealthStatus.Ready,
                stopwatch.Elapsed.TotalMilliseconds,
                null,
                connection.Tools.Select(tool => tool.Name).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new McpHealthResult(
                configuration.Id,
                McpServerHealthStatus.Degraded,
                stopwatch.Elapsed.TotalMilliseconds,
                $"MCP server test timed out after {TestTimeout.TotalSeconds:0} seconds.",
                []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new McpHealthResult(
                configuration.Id,
                McpServerHealthStatus.Degraded,
                stopwatch.Elapsed.TotalMilliseconds,
                SanitizeError(exception.Message, configuration),
                []);
        }
    }

    public async Task DrainAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        List<PoolEntry> entriesToDispose;
        Task[] drainTasks;
        lock (_sync)
        {
            var entries = _entries.Values
                .Where(entry => string.Equals(entry.ServerId, serverId, StringComparison.Ordinal))
                .ToArray();
            foreach (var entry in entries)
            {
                entry.MarkDraining();
            }

            entriesToDispose = entries.Where(entry => entry.ReferenceCount == 0).ToList();
            drainTasks = entries.Select(entry => entry.Drained).ToArray();
        }

        foreach (var entry in entriesToDispose)
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        await Task.WhenAll(drainTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMcpClientConnection> ConnectAsync(ResolvedMcpServerConfiguration configuration)
    {
        try
        {
            return await _connectionFactory.ConnectAsync(configuration, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(McpClientManager));
        }
    }

    /// <summary>
    /// Marks entries of superseded configurations as draining. Only a configuration revision change
    /// invalidates a connection: the workspace path is part of the pool key but a different workspace's
    /// entry is still valid, so switching workspaces keeps its idle connections until idle expiry.
    /// </summary>
    private List<PoolEntry> MarkOlderEntriesDraining(string serverId, long currentRevision)
    {
        var entriesToDispose = new List<PoolEntry>();
        foreach (var entry in _entries.Values.Where(entry =>
                     string.Equals(entry.ServerId, serverId, StringComparison.Ordinal) &&
                     entry.ConfigRevision != currentRevision))
        {
            entry.MarkDraining();
            if (entry.ReferenceCount == 0)
            {
                entriesToDispose.Add(entry);
            }
        }

        return entriesToDispose;
    }

    private ValueTask ReleaseAsync(PoolEntry entry)
    {
        bool disposeNow;
        lock (_sync)
        {
            disposeNow = entry.Release(_idleTimeout, IsDisposed);
        }

        return disposeNow ? new ValueTask(DisposeEntryAsync(entry)) : ValueTask.CompletedTask;
    }

    private async Task ReleaseFailedAcquireAsync(PoolEntry entry)
    {
        lock (_sync)
        {
            entry.ReleaseAndDrain();
        }

        await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    /// <summary>
    /// Same decision as <see cref="ReleaseAsync"/>, but a cancelled acquire has no caller left to await
    /// the teardown, so a dispose that becomes due runs detached.
    /// </summary>
    private void ReleaseCanceledAcquire(PoolEntry entry)
    {
        bool disposeNow;
        lock (_sync)
        {
            disposeNow = entry.Release(_idleTimeout, IsDisposed);
        }

        if (disposeNow)
        {
            _ = DisposeEntryAsync(entry);
        }
    }

    private async Task DisposeIdleEntryAsync(PoolEntry entry)
    {
        lock (_sync)
        {
            if (!entry.TryBeginIdleDispose())
            {
                return;
            }
        }

        await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    private async Task DisposeEntryAsync(PoolEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.Remove(entry.Key, out _))
            {
                return;
            }
        }

        try
        {
            var connection = await entry.ConnectionTask.ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A failed connection has no live transport left to drain.
        }
        finally
        {
            entry.CompleteDrained();
        }
    }

    private static string CreatePoolKey(ResolvedMcpServerConfiguration configuration)
        => string.Join('\0', configuration.Id, configuration.ConfigRevision, configuration.WorkspacePath ?? string.Empty);

    private static string SanitizeError(string error, ResolvedMcpServerConfiguration configuration)
    {
        var sanitized = error;
        foreach (var secret in configuration.Environment.Values.Concat(configuration.Headers.Values)
                     .Where(value => !string.IsNullOrEmpty(value))
                     .Distinct(StringComparer.Ordinal))
        {
            sanitized = sanitized.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return sanitized.Length <= 2048 ? sanitized : sanitized[..2048];
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(IsDisposed, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        PoolEntry[] entries;
        lock (_sync)
        {
            entries = _entries.Values.ToArray();
            foreach (var entry in entries)
            {
                entry.MarkDraining();
            }
        }

        foreach (var entry in entries)
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }

    private sealed class PoolEntry
    {
        private readonly Func<PoolEntry, Task> _idleCallback;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _idleCancellation;

        public PoolEntry(
            string key,
            string serverId,
            long configRevision,
            Task<IMcpClientConnection> connectionTask,
            Func<PoolEntry, Task> idleCallback)
        {
            Key = key;
            ServerId = serverId;
            ConfigRevision = configRevision;
            ConnectionTask = connectionTask;
            _idleCallback = idleCallback;
        }

        public string Key { get; }
        public string ServerId { get; }
        public long ConfigRevision { get; }
        public Task<IMcpClientConnection> ConnectionTask { get; }
        public int ReferenceCount { get; private set; }
        public bool IsDraining { get; private set; }
        public Task Drained => _drained.Task;

        public void Acquire()
        {
            CancelIdleTimer();
            ReferenceCount++;
        }

        /// <summary>
        /// Drops one reference and answers the only question callers have: does this entry now need to be
        /// disposed? A still-referenced entry stays live, and an unreferenced one either starts its idle
        /// timer or becomes due for teardown because it is draining or the pool is shutting down.
        /// </summary>
        public bool Release(TimeSpan idleTimeout, bool poolDisposed)
        {
            if (--ReferenceCount != 0)
            {
                return false;
            }

            if (IsDraining || poolDisposed)
            {
                return true;
            }

            ScheduleIdle(idleTimeout);
            return false;
        }

        /// <summary>A failed acquire leaves no usable connection behind, so the entry never goes idle.</summary>
        public void ReleaseAndDrain()
        {
            ReferenceCount--;
            MarkDraining();
        }

        /// <summary>
        /// The idle timer fires outside the lock, so re-check under it: a new acquire or an explicit drain
        /// may have won the race.
        /// </summary>
        public bool TryBeginIdleDispose()
        {
            if (ReferenceCount != 0 || IsDraining)
            {
                return false;
            }

            MarkDraining();
            return true;
        }

        public void MarkDraining()
        {
            IsDraining = true;
            CancelIdleTimer();
        }

        private void ScheduleIdle(TimeSpan timeout)
        {
            CancelIdleTimer();
            _idleCancellation = new CancellationTokenSource();
            _ = RunIdleTimerAsync(timeout, _idleCancellation.Token);
        }

        public void CompleteDrained()
        {
            _idleCancellation?.Dispose();
            _idleCancellation = null;
            _drained.TrySetResult();
        }

        private void CancelIdleTimer()
        {
            _idleCancellation?.Cancel();
            _idleCancellation?.Dispose();
            _idleCancellation = null;
        }

        private async Task RunIdleTimerAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
                await _idleCallback(this).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }
}
