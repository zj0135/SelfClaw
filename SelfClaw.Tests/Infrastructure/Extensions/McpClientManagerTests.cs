using FluentAssertions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class McpClientManagerTests
{
    [Fact]
    public async Task AcquireAsync_ConcurrentSameKey_ConnectsOnce()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var configuration = CreateConfiguration();

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => manager.AcquireAsync(configuration)));

        factory.ConnectCount.Should().Be(1);
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_CancelingOneWaiter_DoesNotPoisonSharedConnection()
    {
        var factory = new DelayedConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var configuration = CreateConfiguration();
        using var cancellation = new CancellationTokenSource();

        var canceledAcquire = manager.AcquireAsync(configuration, cancellation.Token);
        var survivingAcquire = manager.AcquireAsync(configuration);
        await factory.ConnectStarted.Task;
        cancellation.Cancel();

        var waitForCanceledAcquire = async () => await canceledAcquire;
        await waitForCanceledAcquire.Should().ThrowAsync<OperationCanceledException>();
        factory.Complete();
        await using var lease = await survivingAcquire;

        factory.ConnectCount.Should().Be(1);
        factory.Connection.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_NewRevision_DrainsOldConnectionAfterLeaseRelease()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var oldLease = await manager.AcquireAsync(CreateConfiguration(revision: 1));

        await using var newLease = await manager.AcquireAsync(CreateConfiguration(revision: 2));

        factory.ConnectCount.Should().Be(2);
        factory.Connections[0].DisposeCount.Should().Be(0);
        await oldLease.DisposeAsync();
        factory.Connections[0].DisposeCount.Should().Be(1);
        factory.Connections[1].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task AcquireAsync_WorkspacePath_IsPartOfPoolKey()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        await using var first = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\first"));
        await using var second = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\second"));

        factory.ConnectCount.Should().Be(2);
    }

    [Fact]
    public async Task AcquireAsync_WorkspaceChange_DoesNotDrainAnotherWorkspaceEntry()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var firstWorkspace = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\first"));
        await firstWorkspace.DisposeAsync();

        await using var secondWorkspace = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\second"));
        var backToFirst = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\first"));
        await backToFirst.DisposeAsync();

        factory.ConnectCount.Should().Be(2);
        factory.Connections[0].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseAsync_AfterIdleTimeout_DisposesConnection()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMilliseconds(20));
        var lease = await manager.AcquireAsync(CreateConfiguration());

        await lease.DisposeAsync();
        await WaitUntilAsync(() => factory.Connections[0].DisposeCount == 1);

        factory.Connections[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_WaitsForActiveLease()
    {
        var factory = new FakeConnectionFactory();
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var lease = await manager.AcquireAsync(CreateConfiguration());

        var drainTask = manager.DrainAsync("server");
        await Task.Delay(20);
        drainTask.IsCompleted.Should().BeFalse();
        await lease.DisposeAsync();
        await drainTask;

        factory.Connections[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task TestAsync_WhenConnectionFails_RedactsResolvedSecrets()
    {
        var factory = new FakeConnectionFactory
        {
            ConnectException = new InvalidOperationException("failure included top-secret")
        };
        await using var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var configuration = CreateConfiguration(environment: new Dictionary<string, string>
        {
            ["TOKEN"] = "top-secret"
        });

        var result = await manager.TestAsync(configuration);

        result.Status.Should().Be(McpServerHealthStatus.Degraded);
        result.Error.Should().Be("failure included [redacted]");
    }

    [Fact]
    public async Task DisposeAsync_DisposesActiveConnectionsOnce()
    {
        var factory = new FakeConnectionFactory();
        var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        var lease = await manager.AcquireAsync(CreateConfiguration());

        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await lease.DisposeAsync();

        factory.Connections[0].DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DisposesPooledEntriesConcurrently()
    {
        var factory = new GatedDisposeConnectionFactory();
        var manager = new McpClientManager(factory, TimeSpan.FromMinutes(1));
        await using var first = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\first"));
        await using var second = await manager.AcquireAsync(CreateConfiguration(workspacePath: "C:\\second"));

        var disposal = manager.DisposeAsync().AsTask();

        try
        {
            // Both entries have to be inside DisposeAsync at the same time. A serial teardown would still
            // be waiting for the first connection's gate and would never start the second one.
            await factory.AllDisposalsStarted.WaitAsync(TimeSpan.FromSeconds(5));
            disposal.IsCompleted.Should().BeFalse();
        }
        finally
        {
            factory.ReleaseDisposals();
        }

        await disposal;

        factory.Connections.Should().OnlyContain(connection => connection.DisposeCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    private static ResolvedMcpServerConfiguration CreateConfiguration(
        long revision = 1,
        string? workspacePath = "C:\\work",
        IReadOnlyDictionary<string, string>? environment = null)
        => new(
            "server",
            "Server",
            McpTransportKind.Stdio,
            revision,
            null,
            true,
            null,
            "server.exe",
            [],
            workspacePath,
            environment ?? new Dictionary<string, string>(),
            null,
            null,
            null,
            new Dictionary<string, string>(),
            workspacePath);

    private sealed class FakeConnectionFactory : IMcpClientConnectionFactory
    {
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public List<FakeConnection> Connections { get; } = [];
        public Exception? ConnectException { get; init; }

        public Task<IMcpClientConnection> ConnectAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            var connection = new FakeConnection();
            lock (Connections)
            {
                Connections.Add(connection);
            }

            return Task.FromResult<IMcpClientConnection>(connection);
        }
    }

    private sealed class DelayedConnectionFactory : IMcpClientConnectionFactory
    {
        private readonly TaskCompletionSource<IMcpClientConnection> _connectionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeConnection Connection { get; } = new();

        public Task<IMcpClientConnection> ConnectAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult();
            return _connectionSource.Task;
        }

        public void Complete() => _connectionSource.TrySetResult(Connection);
    }

    private sealed class GatedDisposeConnectionFactory : IMcpClientConnectionFactory
    {
        private const int ExpectedDisposals = 2;
        private readonly object _sync = new();
        private readonly List<GatedDisposeConnection> _connections = [];
        private readonly TaskCompletionSource _allDisposalsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDisposals = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedDisposals;

        public IReadOnlyList<GatedDisposeConnection> Connections
        {
            get { lock (_sync) return [.. _connections]; }
        }

        public Task AllDisposalsStarted => _allDisposalsStarted.Task;

        public void ReleaseDisposals() => _releaseDisposals.TrySetResult();

        public Task<IMcpClientConnection> ConnectAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            var connection = new GatedDisposeConnection(OnDisposalStarted, _releaseDisposals.Task);
            lock (_sync) _connections.Add(connection);
            return Task.FromResult<IMcpClientConnection>(connection);
        }

        private void OnDisposalStarted()
        {
            if (Interlocked.Increment(ref _startedDisposals) == ExpectedDisposals)
            {
                _allDisposalsStarted.TrySetResult();
            }
        }
    }

    private sealed class GatedDisposeConnection(Action onDisposalStarted, Task release) : IMcpClientConnection
    {
        private int _disposeCount;

        public IReadOnlyList<McpClientTool> Tools { get; } = [];
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task PingAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            onDisposalStarted();
            await release;
        }
    }

    private sealed class FakeConnection : IMcpClientConnection
    {
        private int _disposeCount;

        public IReadOnlyList<McpClientTool> Tools { get; } = [];
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task PingAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
