using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// Pins the rule matrix each Direct origin follows, so a change here is a deliberate change to what every
/// capability source and the runtime are allowed to do.
/// </summary>
public sealed class DirectTurnPolicyTests
{
    [Fact]
    public void Interactive_turns_use_their_agent_bindings()
    {
        var policy = DirectTurnPolicy.For(Request(DirectTurnOrigin.Interactive, ceiling: null));

        policy.IsDelegated.Should().BeFalse();
        policy.InheritsHookPlugins.Should().BeFalse();
        policy.InheritedHookPlugins.Should().BeEmpty();
        policy.RequiresCapturedCapabilities.Should().BeFalse();
        policy.ShrinksToCapturedCapabilities.Should().BeFalse();
        policy.RequiresToolExecutionCheckpoint.Should().BeFalse();
        policy.HasFreshUserMessage.Should().BeTrue();
        policy.CanDelegateToSubagent.Should().BeTrue();
        policy.AllowsSkill("alpha/skill").Should().BeTrue();
        policy.AllowsMcpServer(Server("git", revision: 99)).Should().BeTrue();
        policy.DiagnosesRemovedCapabilities.Should().BeFalse();
    }

    [Fact]
    public void Subagent_children_stay_inside_the_ceiling_and_cannot_delegate_further()
    {
        var policy = DirectTurnPolicy.For(Request(DirectTurnOrigin.Subagent, Ceiling()));

        policy.IsDelegated.Should().BeTrue();
        policy.InheritsHookPlugins.Should().BeTrue();
        policy.InheritedHookPlugins.Should().ContainSingle().Which.Id.Should().Be("alpha");
        policy.RequiresCapturedCapabilities.Should().BeTrue();
        policy.ShrinksToCapturedCapabilities.Should().BeFalse();
        policy.RequiresToolExecutionCheckpoint.Should().BeFalse();
        policy.HasFreshUserMessage.Should().BeTrue();
        policy.CanDelegateToSubagent.Should().BeFalse();
        policy.AllowsSkill("alpha/skill").Should().BeTrue();
        policy.AllowsSkill("other/skill").Should().BeFalse();
        policy.AllowsMcpServer(Server("git", revision: 3)).Should().BeTrue();
        policy.AllowsMcpServer(Server("git", revision: 4)).Should().BeFalse();
        policy.DiagnosesRemovedCapabilities.Should().BeFalse();
    }

    [Fact]
    public void Continuations_shrink_to_the_ceiling_and_require_the_execution_checkpoint()
    {
        var policy = DirectTurnPolicy.For(Request(DirectTurnOrigin.Continuation, Ceiling()));

        policy.IsDelegated.Should().BeTrue();
        policy.InheritsHookPlugins.Should().BeTrue();
        policy.InheritedHookPlugins.Should().ContainSingle().Which.Id.Should().Be("alpha");
        policy.RequiresCapturedCapabilities.Should().BeFalse();
        policy.ShrinksToCapturedCapabilities.Should().BeTrue();
        policy.RequiresToolExecutionCheckpoint.Should().BeTrue();
        policy.HasFreshUserMessage.Should().BeFalse();
        policy.CanDelegateToSubagent.Should().BeTrue();
        policy.AllowsSkill("alpha/skill").Should().BeTrue();
        policy.AllowsSkill("other/skill").Should().BeFalse();
        policy.AllowsMcpServer(Server("git", revision: 3)).Should().BeTrue();
        policy.AllowsMcpServer(Server("git", revision: 4)).Should().BeFalse();
        policy.DiagnosesRemovedCapabilities.Should().BeTrue();
    }

    private static DirectCapabilityCeiling Ceiling()
        => new(
            AgentRuntimeDefinition.SystemToolPolicy,
            [new DirectExtensionCapability("alpha", "1.0.0", "hash")],
            [new DirectExtensionCapability("alpha/skill", "1.0.0", "hash")],
            [new DirectMcpCapability("git", 3)],
            ["reviewer"],
            [new DirectExtensionCapability("alpha", "1.0.0", "hash")]);

    private static DirectChatTurnRequest Request(DirectTurnOrigin origin, DirectCapabilityCeiling? ceiling)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new AgentRuntimeDefinition(
                "build", "Build", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                ["alpha"], ["alpha/skill"], ["git"], ["reviewer"], "instructions"),
            [],
            [],
            null,
            ToolPermissionMode.FullAccess,
            ToolApprovalHandler: null,
            new DirectTurnExecutionContext(origin, ceiling, null));

    private static McpServerConfigRecord Server(string id, long revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpServerConfigRecord(
            id,
            id,
            McpTransportKind.Stdio,
            "{}",
            new Dictionary<string, string>(),
            SourcePluginId: null,
            IsEnabled: true,
            revision,
            [],
            McpServerHealthStatus.Ready,
            LastError: null,
            LastCheckedAtUtc: null,
            now,
            now);
    }
}
