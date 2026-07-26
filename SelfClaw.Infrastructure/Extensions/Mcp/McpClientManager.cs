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
            entriesToDispose = MarkOlderEntriesDraining(configuration.Id, key);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new PoolEntry(
                    key,
                    configuration.Id,
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

    private List<PoolEntry> MarkOlderEntriesDraining(string serverId, string currentKey)
    {
        var entriesToDispose = new List<PoolEntry>();
        foreach (var entry in _entries.Values.Where(entry =>
                     string.Equals(entry.ServerId, serverId, StringComparison.Ordinal) &&
                     !string.Equals(entry.Key, currentKey, StringComparison.Ordinal)))
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
        var disposeNow = false;
        lock (_sync)
        {
            if (entry.Release() == 0)
            {
                if (entry.IsDraining || Volatile.Read(ref _disposed) != 0)
                {
                    disposeNow = true;
                }
                else
                {
                    entry.ScheduleIdle(_idleTimeout);
                }
            }
        }

        return disposeNow ? new ValueTask(DisposeEntryAsync(entry)) : ValueTask.CompletedTask;
    }

    private async Task ReleaseFailedAcquireAsync(PoolEntry entry)
    {
        lock (_sync)
        {
            entry.Release();
            entry.MarkDraining();
        }

        await DisposeEntryAsync(entry).ConfigureAwait(false);
    }

    private void ReleaseCanceledAcquire(PoolEntry entry)
    {
        var disposeInBackground = false;
        lock (_sync)
        {
            if (entry.Release() != 0)
            {
                return;
            }

            if (entry.IsDraining || Volatile.Read(ref _disposed) != 0)
            {
                disposeInBackground = true;
            }
            else
            {
                entry.ScheduleIdle(_idleTimeout);
            }
        }

        if (disposeInBackground)
        {
            _ = DisposeEntryAsync(entry);
        }
    }

    private async Task DisposeIdleEntryAsync(PoolEntry entry)
    {
        lock (_sync)
        {
            if (entry.ReferenceCount != 0 || entry.IsDraining)
            {
                return;
            }

            entry.MarkDraining();
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

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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
            Task<IMcpClientConnection> connectionTask,
            Func<PoolEntry, Task> idleCallback)
        {
            Key = key;
            ServerId = serverId;
            ConnectionTask = connectionTask;
            _idleCallback = idleCallback;
        }

        public string Key { get; }
        public string ServerId { get; }
        public Task<IMcpClientConnection> ConnectionTask { get; }
        public int ReferenceCount { get; private set; }
        public bool IsDraining { get; private set; }
        public Task Drained => _drained.Task;

        public void Acquire()
        {
            _idleCancellation?.Cancel();
            _idleCancellation?.Dispose();
            _idleCancellation = null;
            ReferenceCount++;
        }

        public int Release() => --ReferenceCount;

        public void MarkDraining()
        {
            IsDraining = true;
            _idleCancellation?.Cancel();
            _idleCancellation?.Dispose();
            _idleCancellation = null;
        }

        public void ScheduleIdle(TimeSpan timeout)
        {
            _idleCancellation?.Cancel();
            _idleCancellation?.Dispose();
            _idleCancellation = new CancellationTokenSource();
            _ = RunIdleTimerAsync(timeout, _idleCancellation.Token);
        }

        public void CompleteDrained()
        {
            _idleCancellation?.Dispose();
            _idleCancellation = null;
            _drained.TrySetResult();
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
