using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class AsyncHookExecutorTests
{
    [Fact]
    public async Task Same_hook_runs_strictly_fifo()
    {
        var starts = new List<string>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateExecutor((start, _, _, token) =>
        {
            lock (starts)
            {
                starts.Add(start.FileName);
            }

            firstStarted.TrySetResult();
            return WaitAsync(release.Task, token);
        });

        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "h", "first.exe")).Should().BeTrue();
        await firstStarted.Task;
        executor.TryEnqueue(Work("p", "h", "second.exe")).Should().BeTrue();
        await Task.Delay(50);
        lock (starts)
        {
            starts.Should().Equal("first.exe");
        }

        release.SetResult();
        await WaitUntilAsync(() =>
        {
            lock (starts)
            {
                return starts.Count == 2;
            }
        });
        lock (starts)
        {
            starts.Should().Equal("first.exe", "second.exe");
        }

        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Different_hooks_run_in_parallel()
    {
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateExecutor((_, _, _, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            return WaitAsync(release.Task, token);
        });

        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "a", "a.exe")).Should().BeTrue();
        executor.TryEnqueue(Work("p", "b", "b.exe")).Should().BeTrue();

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Enqueue_before_start_is_dropped()
    {
        var leases = new CountingLeaseManager();
        var log = new PluginHookExecutionLog();
        var executor = new AsyncHookExecutor(
            (_, _, _, _) => Task.FromResult(HookTestFactory.Success("")), log, leases);

        executor.TryEnqueue(Work("p", "h", "hook.exe")).Should().BeFalse();

        leases.Live.Should().Be(0);
        log.GetRecent("p").Should().ContainSingle().Which.Outcome.Should().Be("dropped");
    }

    [Fact]
    public async Task Enqueue_after_stop_is_dropped()
    {
        var log = new PluginHookExecutionLog();
        var leases = new CountingLeaseManager();
        var executor = new AsyncHookExecutor(
            (_, _, _, _) => Task.FromResult(HookTestFactory.Success("")), log, leases);
        await executor.StartAsync(CancellationToken.None);
        await executor.StopAsync(CancellationToken.None);

        executor.TryEnqueue(Work("p", "h", "hook.exe")).Should().BeFalse();

        leases.Live.Should().Be(0);
        log.GetRecent("p").Should().ContainSingle().Which.Outcome.Should().Be("dropped");
    }

    [Fact]
    public async Task Lease_acquisition_failure_drops_the_item()
    {
        var log = new PluginHookExecutionLog();
        var executor = new AsyncHookExecutor(
            (_, _, _, _) => Task.FromResult(HookTestFactory.Success("")),
            log,
            new FailingLeaseManager());
        await executor.StartAsync(CancellationToken.None);

        executor.TryEnqueue(Work("p", "h", "hook.exe")).Should().BeFalse();

        log.GetRecent("p").Should().ContainSingle().Which.Outcome.Should().Be("dropped");
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Per_plugin_capacity_drops_the_overflow()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new PluginHookExecutionLog();
        var leases = new CountingLeaseManager();
        var running = 0;
        var workersBusy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new AsyncHookExecutor(
            (_, _, _, token) =>
            {
                if (Interlocked.Increment(ref running) == 4)
                {
                    workersBusy.TrySetResult();
                }

                return WaitAsync(release.Task, token);
            },
            log,
            leases);
        await executor.StartAsync(CancellationToken.None);

        // Fill all four workers first so the queue depth is deterministic.
        for (var index = 0; index < 4; index++)
        {
            executor.TryEnqueue(Work("p", $"running-{index}", "hook.exe")).Should().BeTrue();
        }

        await workersBusy.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 64; index++)
        {
            executor.TryEnqueue(Work("p", $"queued-{index}", "hook.exe")).Should().BeTrue();
        }

        executor.TryEnqueue(Work("p", "overflow", "hook.exe")).Should().BeFalse();
        log.GetRecent("p").Select(entry => entry.Outcome).Should().Contain("dropped");

        release.SetResult();
        await executor.StopAsync(CancellationToken.None);
        leases.Live.Should().Be(0);
    }

    [Fact]
    public async Task Completed_work_releases_its_lease_exactly_once()
    {
        var log = new PluginHookExecutionLog();
        var leases = new CountingLeaseManager();
        var executor = new AsyncHookExecutor(
            (_, _, _, _) => Task.FromResult(HookTestFactory.Success("")),
            log,
            leases);
        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "h", "hook.exe")).Should().BeTrue();

        await WaitUntilAsync(() => log.GetRecent("p").Count == 1);

        log.GetRecent("p").Should().ContainSingle().Which.Outcome.Should().Be("observed");
        leases.Live.Should().Be(0);
        leases.Releases.Should().Be(1);
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Evict_removes_queued_work_and_releases_its_lease()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new PluginHookExecutionLog();
        var leases = new CountingLeaseManager();
        var executor = new AsyncHookExecutor(
            (_, _, _, token) => WaitAsync(release.Task, token),
            log,
            leases);
        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "running", "running.exe")).Should().BeTrue();
        executor.TryEnqueue(Work("p", "queued", "queued.exe")).Should().BeTrue();

        await executor.EvictPluginAsync("p");

        log.GetRecent("p").Select(entry => entry.Outcome).Should().Contain("evicted");
        leases.Live.Should().Be(0);

        release.SetResult();

        // Later work for the same plugin is not affected by the finished eviction.
        executor.TryEnqueue(Work("p", "later", "later.exe")).Should().BeTrue();
        await WaitUntilAsync(() => log.GetRecent("p").Any(entry => entry.HookId == "later"));
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Evict_cancels_in_flight_work()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateExecutor(async (_, _, _, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return HookTestFactory.Success("");
        });
        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "h", "hook.exe")).Should().BeTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await executor.EvictPluginAsync("p").WaitAsync(TimeSpan.FromSeconds(5));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_drops_queued_work_and_releases_leases()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new PluginHookExecutionLog();
        var leases = new CountingLeaseManager();
        var executor = new AsyncHookExecutor(
            (_, _, _, token) => WaitAsync(release.Task, token),
            log,
            leases);
        await executor.StartAsync(CancellationToken.None);
        executor.TryEnqueue(Work("p", "running", "running.exe")).Should().BeTrue();
        executor.TryEnqueue(Work("p", "queued", "queued.exe")).Should().BeTrue();

        release.SetResult();
        await executor.StopAsync(CancellationToken.None);

        log.GetRecent("p").Should().NotBeEmpty();
        leases.Live.Should().Be(0);
    }

    private static AsyncHookExecutor CreateExecutor(
        Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> runner)
        => new(runner, new PluginHookExecutionLog(), new CountingLeaseManager(), NullLogger<AsyncHookExecutor>.Instance);

    private static AsyncHookWork Work(string pluginId, string hookId, string command)
        => new(
            HookTestFactory.CreateHook(pluginId, hookId, PluginHookEvent.RunCompleted),
            "runCompleted",
            ReadOnlyMemory<byte>.Empty,
            Guid.NewGuid(),
            new HookProcessStart(
                command,
                [],
                Path.GetTempPath(),
                new Dictionary<string, string>(StringComparer.Ordinal)));

    private static async Task<HookProcessResult> WaitAsync(Task task, CancellationToken token)
    {
        await task.WaitAsync(token);
        return HookTestFactory.Success("");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class CountingLeaseManager : IPluginVersionLeaseManager
    {
        private readonly object _sync = new();
        private int _live;

        public int Live
        {
            get
            {
                lock (_sync)
                {
                    return _live;
                }
            }
        }

        public int Releases { get; private set; }

        public IDisposable Acquire(string installPath)
        {
            lock (_sync)
            {
                _live++;
            }

            return new Lease(this);
        }

        public Task<IDisposable> AcquireDrainsAsync(
            IReadOnlyList<string> installPaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IDisposable>(new Lease(this));

        public Task DrainAsync(string installPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private void Release()
        {
            lock (_sync)
            {
                _live--;
                Releases++;
            }
        }

        private sealed class Lease(CountingLeaseManager owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.Release();
                }
            }
        }
    }

    private sealed class FailingLeaseManager : IPluginVersionLeaseManager
    {
        public IDisposable Acquire(string installPath)
            => throw new InvalidOperationException("Plugin version is being removed.");

        public Task<IDisposable> AcquireDrainsAsync(
            IReadOnlyList<string> installPaths,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Plugin version is being removed.");

        public Task DrainAsync(string installPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
