using FluentAssertions;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HookNotesTests
{
    private static readonly HookSource Source = new("shell-guard", "deny-dangerous-shell");

    [Fact]
    public void Describe_joins_plugin_and_hook()
        => HookNotes.Describe(Source).Should().Be("shell-guard/deny-dangerous-shell");

    [Fact]
    public void ArgumentsModified_names_the_single_hook_and_includes_the_arguments()
        => HookNotes.ArgumentsModified("""{"command":"ls"}""", [Source])
            .Should().Be(
                "Arguments were modified by hook 'shell-guard/deny-dangerous-shell' before execution: {\"command\":\"ls\"}");

    [Fact]
    public void ArgumentsModified_lists_every_modifier()
        => HookNotes.ArgumentsModified("{}", [Source, new HookSource("other", "hook")])
            .Should().StartWith("Arguments were modified by hooks 'shell-guard/deny-dangerous-shell', 'other/hook' before execution:");

    [Fact]
    public void ArgumentsModified_truncates_the_arguments_json()
    {
        var note = HookNotes.ArgumentsModified(new string('x', 3 * 1024), [Source]);

        note.Length.Should().BeLessThan(3 * 1024);
        note.Should().EndWith(new string('x', HookNotes.MaximumArgumentsJsonBytes));
    }

    [Fact]
    public void Notices_use_the_documented_texts()
    {
        HookNotes.ContextAdded(Source, 12).Should().Be("Hook 'shell-guard/deny-dangerous-shell' added context (12 chars).");
        HookNotes.FailureIgnored(Source, "timedOut").Should().Be("Hook 'shell-guard/deny-dangerous-shell' failed (timedOut); ignored.");
        HookNotes.ContextDropped(Source).Should().Be(
            "Hook 'shell-guard/deny-dangerous-shell' context was dropped because the turn context limit (64 KiB) was reached.");
        HookNotes.PluginSkipped("shell-guard", "permissions require confirmation")
            .Should().Be(
                "Plugin 'shell-guard' declares hooks but was skipped (permissions require confirmation); its hooks are not active in this turn.");
        HookNotes.InheritedHookPluginBlocked("shell-guard")
            .Should().Be("Inherited hook plugin 'shell-guard' is unavailable or changed since delegation; the turn was blocked.");
    }

    [Fact]
    public void Blocked_uses_a_default_reason()
    {
        HookNotes.Blocked(Source, null).Should().Be("Blocked by hook 'shell-guard/deny-dangerous-shell': No reason given.");
        HookNotes.Blocked(Source, "dangerous").Should().Be("Blocked by hook 'shell-guard/deny-dangerous-shell': dangerous");
    }

    [Fact]
    public void BlockedByFailure_appends_the_detail()
        => HookNotes.BlockedByFailure(Source, "timedOut", "Hook timed out after 10 seconds.")
            .Should().Be(
                "Hook 'shell-guard/deny-dangerous-shell' failed (timedOut) and is configured to block: Hook timed out after 10 seconds.");

    [Theory]
    [InlineData(SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Succeeded, "succeeded")]
    [InlineData(SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Failed, "failed")]
    [InlineData(SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Truncated, "truncated")]
    [InlineData(SelfClaw.Core.Runtime.Agent.RunCompletionStatus.Blocked, "blocked")]
    public void RunStatus_uses_camel_case_names(
        SelfClaw.Core.Runtime.Agent.RunCompletionStatus status,
        string expected)
        => HookNotes.RunStatus(status).Should().Be(expected);
}
