using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Models;

internal sealed record SubagentTaskCreation(
    ConversationRecord ChildConversation,
    MessageRecord TaskMessage,
    SubagentTaskRecord Task);
