using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

/// <summary>
/// Translates a CLI agent's raw stdout into <see cref="AgentStreamEvent"/>s. Implementations buffer
/// arbitrary text chunks internally and split on newlines, so a caller may feed whole lines, partial
/// lines, or multiple lines at once (plan.md §5). A line that fails to parse is surfaced verbatim as a
/// <see cref="RawOutputEvent"/> rather than dropped, preserving diagnostics.
/// <para>
/// The parser is stateful and single-use per run: it tracks session id, block ids and tool-call ids
/// across the stream. Create a fresh instance for each turn.
/// </para>
/// </summary>
public interface IAgentStreamParser
{
    /// <summary>
    /// Feeds a chunk of stdout text and returns the events decoded from any complete lines it
    /// contains. Incomplete trailing text is buffered until the next <see cref="Feed"/> or
    /// <see cref="Flush"/>.
    /// </summary>
    IEnumerable<AgentStreamEvent> Feed(string chunk);

    /// <summary>
    /// Processes any buffered trailing text that was not newline-terminated (e.g. the final line when
    /// the stream ends without a newline) and returns its events. Call once after stdout completes.
    /// </summary>
    IEnumerable<AgentStreamEvent> Flush();
}
