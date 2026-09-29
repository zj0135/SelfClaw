using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

/// <summary>
/// The single place that answers "what may this turn do". The three Direct origins differ in which
/// capability boundary applies, whether a captured capability must still resolve, and which turn-scoped
/// inputs exist; those rules used to be re-derived at eleven call sites across seven files. The policy is a
/// pure function of the request, so every consumer reads the same answer.
/// </summary>
internal sealed record DirectTurnPolicy(DirectTurnOrigin Origin, DirectCapabilityCeiling? Ceiling)
{
    internal static DirectTurnPolicy For(DirectChatTurnRequest request)
        => new(request.ExecutionContext.Origin, request.ExecutionContext.CapabilityCeiling);

    /// <summary>An interactive turn is bounded by its Agent's bindings; a delegated turn by the captured ceiling.</summary>
    internal bool IsDelegated => Origin is not DirectTurnOrigin.Interactive;

    /// <summary>Hook Plugins captured by the parent turn keep enforcing their policy in delegated turns.</summary>
    internal bool InheritsHookPlugins => Origin is DirectTurnOrigin.Subagent or DirectTurnOrigin.Continuation;

    /// <summary>
    /// The hook Plugins an inherited turn must keep enforcing. They are policy rather than capability, so a
    /// missing or changed one blocks the turn instead of being filtered out.
    /// </summary>
    internal IReadOnlyList<DirectExtensionCapability> InheritedHookPlugins
        => InheritsHookPlugins ? Ceiling?.HookPlugins ?? [] : [];

    /// <summary>A Subagent child must still satisfy the captured ceiling, so a missing or changed capability is fatal.</summary>
    internal bool RequiresCapturedCapabilities => Origin == DirectTurnOrigin.Subagent;

    /// <summary>A continuation only shrinks: a capability that changed since delegation is dropped with a diagnostic.</summary>
    internal bool ShrinksToCapturedCapabilities => Origin == DirectTurnOrigin.Continuation;

    /// <summary>A continuation must have committed its durable execution checkpoint before any tool runs.</summary>
    internal bool RequiresToolExecutionCheckpoint => Origin == DirectTurnOrigin.Continuation;

    /// <summary>A continuation resumes the parent's completion batch instead of a fresh user message.</summary>
    internal bool HasFreshUserMessage => Origin != DirectTurnOrigin.Continuation;

    /// <summary>A Subagent child cannot delegate further.</summary>
    internal bool CanDelegateToSubagent => Origin != DirectTurnOrigin.Subagent;

    /// <summary>Interactive turns see every Skill their Agent binds; delegated turns only those inside the ceiling.</summary>
    internal bool AllowsSkill(string skillId)
        => !IsDelegated ||
           Ceiling?.Skills.Any(captured =>
               string.Equals(captured.Id, skillId, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>MCP servers follow the same boundary and must also be unchanged since capture.</summary>
    internal bool AllowsMcpServer(McpServerConfigRecord server)
        => !IsDelegated ||
           (Ceiling is { } ceiling && DirectCapabilityRules.IsMcpCurrent(server, ceiling));

    /// <summary>Only a continuation reports a removed capability as a degradation; a Subagent child fails instead.</summary>
    internal bool DiagnosesRemovedCapabilities => Origin == DirectTurnOrigin.Continuation;
}
