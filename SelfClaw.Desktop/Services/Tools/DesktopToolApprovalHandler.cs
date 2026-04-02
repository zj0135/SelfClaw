using System.Collections.Concurrent;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopToolApprovalHandler : IToolApprovalHandler
{
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pendingApprovals = new();

    public Task<bool> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (_pendingApprovals.TryRemove(request.ToolExecutionId, out var pending))
                {
                    pending.CompletionSource.TrySetCanceled(cancellationToken);
                    pending.CancellationRegistration.Dispose();
                }
            });
        }

        var pendingApproval = new PendingApproval(completionSource, registration);
        if (!_pendingApprovals.TryAdd(request.ToolExecutionId, pendingApproval))
        {
            registration.Dispose();
            throw new InvalidOperationException($"Tool approval for '{request.ToolExecutionId}' is already pending.");
        }

        return AwaitApprovalAsync(request.ToolExecutionId, pendingApproval);
    }

    public bool TryResolve(Guid toolExecutionId, bool approved)
    {
        if (!_pendingApprovals.TryRemove(toolExecutionId, out var pending))
        {
            return false;
        }

        pending.CancellationRegistration.Dispose();
        return pending.CompletionSource.TrySetResult(approved);
    }

    private static async Task<bool> AwaitApprovalAsync(Guid toolExecutionId, PendingApproval pendingApproval)
    {
        try
        {
            return await pendingApproval.CompletionSource.Task;
        }
        finally
        {
            pendingApproval.CancellationRegistration.Dispose();
        }
    }

    private sealed record PendingApproval(
        TaskCompletionSource<bool> CompletionSource,
        CancellationTokenRegistration CancellationRegistration);
}
