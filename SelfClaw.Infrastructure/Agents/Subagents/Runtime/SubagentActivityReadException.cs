namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal sealed class SubagentActivityReadException(string errorCode) : InvalidOperationException(errorCode)
{
    internal string ErrorCode { get; } = errorCode;
}
