namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// A tool/function invocation started by the agent. For CLI agents this is observational
/// (the CLI executes the tool itself); <paramref name="ToolCallId"/> correlates with the
/// matching <see cref="ToolCallCompletedEvent"/>.
/// </summary>
public sealed record ToolCallStartedEvent(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    ToolCallKind Kind,
    ToolSourceKind? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null) : AgentStreamEvent;
