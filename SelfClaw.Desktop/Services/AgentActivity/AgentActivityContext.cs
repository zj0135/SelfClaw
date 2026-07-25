using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.AgentActivity;

public sealed record AgentActivityContext(
    Guid TurnId,
    Guid ConversationId,
    string ConversationTitle,
    string AgentId,
    string AgentName,
    AgentExecutionMode ExecutionMode,
    DateTimeOffset StartedAtUtc);
