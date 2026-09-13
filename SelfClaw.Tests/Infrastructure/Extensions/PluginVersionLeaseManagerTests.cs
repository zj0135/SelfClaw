using FluentAssertions;
using SelfClaw.Infrastructure.Extensions;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class PluginVersionLeaseManagerTests
{
    [Fact]
    public async Task DrainAsync_waits_for_active_lease_and_blocks_new_acquires()
    {
        var manager = new PluginVersionLeaseManager();
        var path = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var lease = manager.Acquire(path);

        var drainTask = manager.DrainAsync(path);

        drainTask.IsCompleted.Should().BeFalse();
        var acquire = () => manager.Acquire(path);
        acquire.Should().Throw<InvalidOperationException>();

        lease.Dispose();
        await drainTask;
        manager.Acquire(path).Dispose();
    }

    [Fact]
    public async Task DrainAsync_cancellation_allows_new_acquires_when_no_other_drain_is_pending()
    {
        var manager = new PluginVersionLeaseManager();
        var path = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        using var lease = manager.Acquire(path);
        using var cancellation = new CancellationTokenSource();

        var drainTask = manager.DrainAsync(path, cancellation.Token);
        cancellation.Cancel();

        var waitForDrain = async () => await drainTask;
        await waitForDrain.Should().ThrowAsync<OperationCanceledException>();
        manager.Acquire(path).Dispose();
    }

    [Fact]
    public async Task DrainAsync_cancellation_keeps_acquires_blocked_for_another_pending_drain()
    {
        var manager = new PluginVersionLeaseManager();
        var path = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var lease = manager.Acquire(path);
        using var cancellation = new CancellationTokenSource();

        var canceledDrain = manager.DrainAsync(path, cancellation.Token);
        var activeDrain = manager.DrainAsync(path);
        cancellation.Cancel();

        var waitForCanceledDrain = async () => await canceledDrain;
        await waitForCanceledDrain.Should().ThrowAsync<OperationCanceledException>();
        var acquire = () => manager.Acquire(path);
        acquire.Should().Throw<InvalidOperationException>();

        lease.Dispose();
        await activeDrain;
    }

    [Fact]
    public async Task AcquireDrainsAsync_blocks_all_paths_until_the_drain_lease_is_released()
    {
        var manager = new PluginVersionLeaseManager();
        var firstPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var secondPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

        using var drain = await manager.AcquireDrainsAsync([firstPath, secondPath]);

        var acquireFirst = () => manager.Acquire(firstPath);
        var acquireSecond = () => manager.Acquire(secondPath);
        acquireFirst.Should().Throw<InvalidOperationException>();
        acquireSecond.Should().Throw<InvalidOperationException>();

        drain.Dispose();
        manager.Acquire(firstPath).Dispose();
        manager.Acquire(secondPath).Dispose();
    }
}
