using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Interfaces;

public interface IToolApprovalHandler
{
    Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically removes a still-pending approval. Returns false when the request was already
    /// decided or cancelled, so a late click can never resurrect an executed call.
    /// </summary>
    bool TryResolve(Guid toolExecutionId, ToolApprovalDecision decision);
}
