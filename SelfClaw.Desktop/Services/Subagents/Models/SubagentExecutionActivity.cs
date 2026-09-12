namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentExecutionActivity(
    long Revision,
    string Phase,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    bool TerminalObserved,
    string? RecordingError);
