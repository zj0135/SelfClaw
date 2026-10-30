namespace SelfClaw.Core.Runtime;

/// <summary>Skipped means the user left this prompt unanswered, either by skipping the batch or by omitting it.</summary>
public sealed record UserQuestionPromptAnswer(
    Guid PromptId,
    IReadOnlyList<string> SelectedLabels,
    string? OtherText,
    bool Skipped);
