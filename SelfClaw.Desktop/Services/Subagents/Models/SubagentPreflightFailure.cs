namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentPreflightFailure(
    string ErrorCode,
    string ErrorMessage);
