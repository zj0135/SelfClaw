namespace SelfClaw.Core.Runtime;

/// <summary>One <c>ask_user</c> call: a batch of prompts the user answers as a whole.</summary>
public sealed record UserQuestionRequest(
    Guid RequestId,
    IReadOnlyList<UserQuestionPrompt> Prompts,
    Guid? ConversationId = null,
    Guid? TurnId = null);
