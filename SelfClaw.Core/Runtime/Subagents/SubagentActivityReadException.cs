namespace SelfClaw.Core.Runtime;

public sealed class SubagentActivityReadException(string errorCode) : InvalidOperationException(errorCode)
{
    internal string ErrorCode { get; } = errorCode;
}
