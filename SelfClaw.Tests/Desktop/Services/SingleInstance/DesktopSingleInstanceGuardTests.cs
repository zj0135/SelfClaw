using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Desktop.Services.SingleInstance;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.SingleInstance;

public sealed class DesktopSingleInstanceGuardTests
{
    [Fact]
    public Task Second_guard_cannot_acquire_while_the_first_holds_the_lock() => WpfDispatcherTest.RunAsync(async () =>
    {
        var instanceName = CreateInstanceName();
        using var primary = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        primary.TryAcquire(TimeSpan.Zero).Should().BeTrue();

        using var second = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        // 命名 mutex 是按线程递归的：必须换一个线程去试，才能代表真正的第二个进程。
        var acquired = await Task.Run(() => second.TryAcquire(TimeSpan.Zero));

        acquired.Should().BeFalse();
    });

    [Fact]
    public Task Signal_reaches_the_running_instance_through_its_dispatcher() => WpfDispatcherTest.RunAsync(async () =>
    {
        var instanceName = CreateInstanceName();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var primary = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        primary.TryAcquire(TimeSpan.Zero).Should().BeTrue();
        primary.StartListening();
        primary.SetActivationHandler(() => activated.TrySetResult());

        using var second = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        (await Task.Run(() => second.SignalPrimaryInstance())).Should().BeTrue();

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Fact]
    public Task Signal_arriving_before_the_window_exists_is_replayed_once_it_does() => WpfDispatcherTest.RunAsync(async () =>
    {
        var instanceName = CreateInstanceName();
        using var primary = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        primary.TryAcquire(TimeSpan.Zero).Should().BeTrue();
        primary.StartListening();

        using var second = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        (await Task.Run(() => second.SignalPrimaryInstance())).Should().BeTrue();

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetActivationHandler(() => activated.TrySetResult());

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });

    [Fact]
    public Task Signal_without_a_listening_instance_reports_failure() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var guard = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, CreateInstanceName());

        var signaled = await Task.Run(() => guard.SignalPrimaryInstance());

        signaled.Should().BeFalse();
    });

    [Fact]
    public Task Dispose_releases_the_lock_for_the_next_process() => WpfDispatcherTest.RunAsync(async () =>
    {
        var instanceName = CreateInstanceName();
        var first = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);
        first.TryAcquire(TimeSpan.Zero).Should().BeTrue();
        first.Dispose();

        using var next = new DesktopSingleInstanceGuard(Dispatcher.CurrentDispatcher, instanceName);

        (await Task.Run(() => next.TryAcquire(TimeSpan.FromSeconds(2)))).Should().BeTrue();
    });

    private static string CreateInstanceName() => $"SelfClaw.Tests.SingleInstance.{Guid.NewGuid():N}";
}
