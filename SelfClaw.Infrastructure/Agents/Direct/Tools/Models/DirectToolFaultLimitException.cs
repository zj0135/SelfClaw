namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

/// <summary>
/// Thrown when one tool keeps faulting past the turn's consecutive-fault budget. It stops the turn with a
/// message that names the cause, and carries the tool's own exception so the transcript still shows the
/// root cause and its stack instead of the abort site.
/// </summary>
internal sealed class DirectToolFaultLimitException(string toolName, int consecutiveFaults, Exception toolFault)
    : Exception(
        $"Tool '{toolName}' faulted {consecutiveFaults} times consecutively, so the turn was stopped. " +
        $"Last error: {toolFault.Message}",
        toolFault);
