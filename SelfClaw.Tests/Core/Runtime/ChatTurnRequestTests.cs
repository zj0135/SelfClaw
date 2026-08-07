using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Tests.Core.Runtime;

public sealed class ChatTurnRequestTests
{
    [Fact]
    public void Constructor_rejects_an_empty_turn_id()
    {
        var action = () => new DirectChatTurnRequest(
            Guid.Empty,
            Guid.NewGuid(),
            WorkspaceRoot: null,
            new AgentRuntimeDefinition(
                "build",
                "Builder",
                "test",
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                [],
                string.Empty),
            Messages: [],
            ModelProfileId: null,
            ToolPermissionMode.RequireApproval,
            ToolApprovalHandler: null,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));

        action.Should().Throw<ArgumentException>()
            .WithParameterName("TurnId");
    }
}
