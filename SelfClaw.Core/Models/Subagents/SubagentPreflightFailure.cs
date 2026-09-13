namespace SelfClaw.Core.Models;

public sealed record SubagentPreflightFailure(
    string ErrorCode,
    string ErrorMessage);
