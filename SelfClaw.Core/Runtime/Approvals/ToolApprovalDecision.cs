namespace SelfClaw.Core.Runtime;

/// <summary>
/// The single decision a pending tool approval can produce. <see cref="Superseded"/> is a
/// targeted revocation of the pending request, not a user answer: it closes the approval bar
/// and toast without letting a later click execute the call.
/// </summary>
public enum ToolApprovalDecision
{
    Approved = 0,
    Denied = 1,
    Superseded = 2
}
