using SelfClaw.Desktop.Services.Notifications;
using FluentAssertions;

namespace SelfClaw.Tests.Desktop.Services;

public sealed class DesktopNotificationArgumentsTests
{
    [Theory]
    [InlineData(DesktopNotificationArguments.ApproveToolAction)]
    [InlineData(DesktopNotificationArguments.RejectToolAction)]
    public void Build_and_parse_round_trip_tool_approval_actions(string action)
    {
        var executionId = Guid.NewGuid();

        var encoded = DesktopNotificationArguments.Build(
            (DesktopNotificationArguments.ActionKey, action),
            (DesktopNotificationArguments.ToolExecutionIdKey, executionId.ToString("D")));
        var parsed = DesktopNotificationArguments.Parse(encoded);

        parsed[DesktopNotificationArguments.ActionKey].Should().Be(action);
        parsed[DesktopNotificationArguments.ToolExecutionIdKey].Should().Be(executionId.ToString("D"));
    }

    [Fact]
    public void Build_and_parse_round_trip_the_automation_run_action()
    {
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        var encoded = DesktopNotificationArguments.Build(
            (DesktopNotificationArguments.ActionKey, DesktopNotificationArguments.OpenAutomationRunAction),
            (DesktopNotificationArguments.TaskIdKey, taskId.ToString("D")),
            (DesktopNotificationArguments.RunIdKey, runId.ToString("D")));
        var parsed = DesktopNotificationArguments.Parse(encoded);

        parsed[DesktopNotificationArguments.ActionKey].Should().Be(DesktopNotificationArguments.OpenAutomationRunAction);
        parsed[DesktopNotificationArguments.TaskIdKey].Should().Be(taskId.ToString("D"));
        parsed[DesktopNotificationArguments.RunIdKey].Should().Be(runId.ToString("D"));
    }

    [Fact]
    public void Build_and_parse_escape_values_and_treat_keys_case_insensitively()
    {
        var encoded = DesktopNotificationArguments.Build(("Action", "open app & inspect"));

        var parsed = DesktopNotificationArguments.Parse(encoded);

        parsed["action"].Should().Be("open app & inspect");
    }
}
