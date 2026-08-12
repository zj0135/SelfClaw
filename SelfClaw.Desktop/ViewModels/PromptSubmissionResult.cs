namespace SelfClaw.Desktop.ViewModels;

public sealed record PromptSubmissionResult(
    bool Accepted,
    string? Error = null);
