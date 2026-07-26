using System.Collections.Concurrent;
using FluentAssertions;
using SelfClaw.Infrastructure.Extensions;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class ExtensionStateChangeNotifierTests
{
    [Fact]
    public void Advance_assigns_a_unique_revision_to_every_concurrent_change()
    {
        var notifier = new ExtensionStateChangeNotifier();
        var observed = new ConcurrentBag<long>();
        notifier.StateChanged += observed.Add;

        Parallel.For(0, 1000, _ => notifier.Advance());

        notifier.CurrentRevision.Should().Be(1000);
        observed.Should().HaveCount(1000).And.OnlyHaveUniqueItems();
        observed.Should().Contain(Enumerable.Range(1, 1000).Select(value => (long)value));
    }

    [Fact]
    public void AdvanceTo_only_publishes_when_the_revision_moves_forward()
    {
        var notifier = new ExtensionStateChangeNotifier();
        var observed = new List<long>();
        notifier.StateChanged += observed.Add;

        notifier.AdvanceTo(5).Should().Be(5);
        notifier.AdvanceTo(3).Should().Be(5);

        observed.Should().Equal(5);
    }

    [Fact]
    public void Advance_isolates_subscriber_failures_and_notifies_remaining_subscribers()
    {
        var notifier = new ExtensionStateChangeNotifier();
        var observed = new List<long>();
        notifier.StateChanged += _ => throw new InvalidOperationException("fixture subscriber failure");
        notifier.StateChanged += observed.Add;

        var revision = notifier.Advance();

        revision.Should().Be(1);
        observed.Should().Equal(1);
    }
}
