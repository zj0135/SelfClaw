namespace SelfClaw.Core.Runtime;

public interface IToolApprovalHandler
{
    Task<bool> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}
