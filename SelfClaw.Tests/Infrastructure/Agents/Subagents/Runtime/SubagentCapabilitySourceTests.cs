using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Subagents.Runtime;

public sealed class SubagentCapabilitySourceTests
{
    [Fact]
    public void CreateTools_exposes_four_tools_only_to_an_eligible_parent()
    {
        var source = new SubagentCapabilitySource(new NoOpCoordinator());
        var ceiling = CreateCeiling();

        var tools = source.CreateTools(CreateRequest(DirectTurnOrigin.Interactive, ["reviewer"]), ceiling);
        var noneForUnbound = source.CreateTools(CreateRequest(DirectTurnOrigin.Interactive, []), ceiling);
        var noneForChild = source.CreateTools(CreateRequest(DirectTurnOrigin.Subagent, ["reviewer"]), ceiling);

        tools.Select(item => item.Tool.Name).Should().Equal(
            SubagentCapabilitySource.DelegateToolName,
            SubagentCapabilitySource.GetTaskToolName,
            SubagentCapabilitySource.CancelTaskToolName,
            SubagentCapabilitySource.RetryTaskToolName);
        noneForUnbound.Should().BeEmpty();
        noneForChild.Should().BeEmpty();
    }

    private static DirectChatTurnRequest CreateRequest(
        DirectTurnOrigin origin,
        IReadOnlyList<string> subagentIds)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new DirectChatTurnRequest(
            Guid.NewGuid(),
            conversationId,
            WorkspaceRoot: null,
            new AgentRuntimeDefinition(
                "build",
                "Build",
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                subagentIds,
                string.Empty),
            [new MessageRecord(
                Guid.NewGuid(),
                conversationId,
                MessageRole.User,
                "work",
                MessageStatus.Completed,
                now,
                now)],
            Guid.NewGuid(),
            ToolPermissionMode.FullAccess,
            ToolApprovalHandler: null,
            new DirectTurnExecutionContext(origin, origin == DirectTurnOrigin.Interactive ? null : CreateCeiling(), null));
    }

    private static DirectCapabilityCeiling CreateCeiling()
        => new(AgentRuntimeDefinition.SystemToolPolicy, [], [], [], ["reviewer"]);

    private sealed class NoOpCoordinator : ISubagentTaskCoordinator
    {
        public Task<SubagentTaskView> StartAsync(
            SubagentTaskStartRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView?> GetAsync(
            SubagentTaskQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView> CancelAsync(
            SubagentTaskCommand command,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView> RetryAsync(
            SubagentTaskRetryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
