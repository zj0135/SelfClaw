using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Automation;

namespace SelfClaw.Tests.Desktop.Services.Automation;

public sealed class AutomationRunNotifierTests
{
    [Theory]
    [InlineData(AutomationRunStatus.Failed, false, true)]
    [InlineData(AutomationRunStatus.Interrupted, false, true)]
    [InlineData(AutomationRunStatus.Completed, false, false)]
    [InlineData(AutomationRunStatus.Completed, true, true)]
    [InlineData(AutomationRunStatus.Cancelled, true, false)]
    [InlineData(AutomationRunStatus.Skipped, true, false)]
    [InlineData(AutomationRunStatus.Running, true, false)]
    public void Failure_and_interrupted_runs_always_notify_while_success_is_opt_in(
        AutomationRunStatus status,
        bool notifyOnSuccess,
        bool expected)
        => AutomationRunNotifier.ShouldNotify(status, notifyOnSuccess).Should().Be(expected);
}
