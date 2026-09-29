using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;

/// <summary>
/// The Subagent capability of one turn: the catalog section that advertises the allowlist, and the
/// delegation tools built over it.
/// </summary>
internal sealed record SubagentCapabilities(
    IReadOnlyList<string> Instructions,
    IReadOnlyList<DirectToolBinding> Tools);
