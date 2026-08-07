namespace SelfClaw.Core.Models;

public sealed record SubagentTaskCreation(
    ConversationRecord ChildConversation,
    MessageRecord TaskMessage,
    SubagentTaskRecord Task,
    SubagentTaskCompletion? InitialCompletion = null);
