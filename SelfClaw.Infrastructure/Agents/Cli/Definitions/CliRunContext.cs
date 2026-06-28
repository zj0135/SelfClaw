using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions;

/// <summary>
/// Per-turn inputs that a <see cref="CliAgentDefinition"/> consumes when building the command line and
/// prompt for a single run. Produced by <c>CliAgentChatRuntime</c> (阶段 5) from the chat request and
/// the persisted session state.
/// </summary>
public sealed record CliRunContext
{
    /// <summary>The agent being launched; lets a shared definition vary behaviour if needed.</summary>
    public required CliAgentKind AgentKind { get; init; }

    /// <summary>The absolute workspace directory the agent runs in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// A previously persisted session id to resume, or <c>null</c> for a fresh conversation.
    /// For <see cref="ResumeStrategy.Specified"/> the runtime supplies the id it minted on the first
    /// turn; for <see cref="ResumeStrategy.CapturedFromStream"/> it is the id captured from an earlier run.
    /// </summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// A freshly minted session id used by <see cref="ResumeStrategy.Specified"/> when there is nothing
    /// to resume (first turn). Ignored by <see cref="ResumeStrategy.CapturedFromStream"/> agents.
    /// </summary>
    public string? NewSessionId { get; init; }

    /// <summary>The assembled system prompt / instructions, injected per the agent's mechanism.</summary>
    public string? SystemPrompt { get; init; }
}
