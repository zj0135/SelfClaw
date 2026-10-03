using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Tests.TestDoubles;

internal static class PresentationHistory
{
    internal static ConversationTurnRecord Turn(MessageRecord message,
        ConversationTurnStatus status = ConversationTurnStatus.Succeeded,
        string? errorMessage = null,
        TurnUsage? usage = null)
        => new(message.TurnId, message.ConversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive,
            status, message.CreatedAtUtc, status == ConversationTurnStatus.Running ? null : message.UpdatedAtUtc,
            errorMessage, usage);

    internal static IReadOnlyList<ConversationTurnRecord> Turns(IReadOnlyList<MessageRecord> messages)
        => messages.GroupBy(message => message.TurnId).Select(group => Turn(group.First())).ToArray();
}
