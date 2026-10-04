using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// The single host-side verdict for "may this conversation steer right now". Acceptance, promote
/// and the pushed queue state all read it, so the UI never re-derives permission from run flags.
/// </summary>
internal static class ConversationInputSteerPolicy
{
    internal static (bool CanSteer, string? Reason, Guid? TargetTurnId) Evaluate(ConversationRunHandle? handle, bool steerEnabled)
    {
        if (handle is null) return (false, null, null);
        if (!steerEnabled) return (false, ConversationInputReason.SteerNotEnabled, null);
        if (handle.Origin == DirectTurnOrigin.Continuation) return (false, ConversationInputReason.ContinuationActive, null);
        if (handle.Mode != AgentExecutionMode.Direct) return (false, ConversationInputReason.DirectOnly, null);
        if (handle.InputSession is not { } session) return (false, ConversationInputReason.DirectOnly, null);
        if (session.SteerIntakeClosed) return (false, ConversationInputReason.TargetTurnClosed, null);
        if (session.SteerBlockReason is { } blocked) return (false, blocked, null);
        return (true, null, handle.TurnId);
    }
}
