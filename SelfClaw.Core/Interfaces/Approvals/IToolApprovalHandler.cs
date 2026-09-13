using SelfClaw.Core.Runtime;

namespace SelfClaw.Core.Interfaces;

public interface IToolApprovalHandler
{
    Task<bool> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}
