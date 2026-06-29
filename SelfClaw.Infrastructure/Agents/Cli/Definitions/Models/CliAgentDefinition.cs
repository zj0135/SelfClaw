using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Definitions.Models;

/// <summary>
/// A declarative description of how to drive one CLI agent: how to build its argument vector, how the
/// prompt is delivered, which stream format its stdout uses, and how sessions resume (plan.md §3 阶段 4,
/// mirroring Open Design's <c>runtimes/defs/*</c>). Definitions are immutable and registered in
/// <see cref="CliAgentRegistry"/> keyed by <see cref="CliAgentKind"/>.
/// </summary>
public sealed record CliAgentDefinition
{
    /// <summary>The agent this definition drives.</summary>
    public required CliAgentKind Kind { get; init; }

    /// <summary>The bare executable name resolved against PATH (e.g. <c>claude</c>).</summary>
    public required string Command { get; init; }

    /// <summary>How the prompt text is delivered to the process (plan.md §5).</summary>
    public required CliInputFormat InputFormat { get; init; }

    /// <summary>The stdout stream format, selecting the matching <c>IAgentStreamParser</c>.</summary>
    public required CliStreamFormat StreamFormat { get; init; }

    /// <summary>How the conversation's session id is established and resumed across turns.</summary>
    public required ResumeStrategy ResumeStrategy { get; init; }

    /// <summary>
    /// Builds the argument vector for a turn, given the resolved run context (working directory,
    /// resume/new session id, etc.). Excludes the executable itself — that is <see cref="Command"/>.
    /// </summary>
    public required Func<CliRunContext, IReadOnlyList<string>> BuildArgs { get; init; }

    /// <summary>
    /// Builds the prompt payload written to stdin for a turn. For <see cref="CliInputFormat.StreamJson"/>
    /// this is a single JSONL <c>user</c> message line; the runtime closes stdin afterwards to signal EOF.
    /// </summary>
    public required Func<string, IReadOnlyList<string>> BuildStdinLines { get; init; }
}
