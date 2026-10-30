namespace SelfClaw.Core.Runtime;

/// <summary>One tab of the dock. PromptId is stable so an answer can be matched back to its question.</summary>
public sealed record UserQuestionPrompt(
    Guid PromptId,
    string Question,
    string? Header,
    IReadOnlyList<UserQuestionOption> Options,
    bool MultiSelect);
