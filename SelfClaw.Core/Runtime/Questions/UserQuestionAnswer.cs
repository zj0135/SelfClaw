namespace SelfClaw.Core.Runtime;

public sealed record UserQuestionAnswer(
    Guid RequestId,
    IReadOnlyList<UserQuestionPromptAnswer> Answers,
    bool Skipped);
