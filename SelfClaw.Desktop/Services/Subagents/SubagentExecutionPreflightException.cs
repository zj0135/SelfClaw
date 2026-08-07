namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentExecutionPreflightException : InvalidOperationException
{
    internal SubagentExecutionPreflightException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}
