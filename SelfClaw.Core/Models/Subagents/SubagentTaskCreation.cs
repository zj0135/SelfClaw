namespace SelfClaw.Core.Models;

public sealed record SubagentTaskCreation(
    ConversationRecord ChildConversation,
    SubagentTaskRecord Task,
    SubagentTaskCompletion? InitialCompletion = null);
