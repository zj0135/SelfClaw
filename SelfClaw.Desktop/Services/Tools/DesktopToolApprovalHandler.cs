using System.Collections.Concurrent;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopToolApprovalHandler : IToolApprovalHandler
{
    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, PendingApproval> _pendingApprovals = new();
    private readonly TimeSpan _approvalTimeout;

    public DesktopToolApprovalHandler()
        : this(DefaultApprovalTimeout)
    {
    }

    internal DesktopToolApprovalHandler(TimeSpan approvalTimeout)
    {
        if (approvalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(approvalTimeout));
        }

        _approvalTimeout = approvalTimeout;
    }

    public event Action<ToolApprovalRequest>? ApprovalRequested;
    public event Action<ToolApprovalRequest>? ApprovalExpired;

    public Task<bool> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var timeoutSource = new CancellationTokenSource(_approvalTimeout);
        CancellationTokenRegistration timeoutRegistration = default;

        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(() =>
            {
                if (_pendingApprovals.TryRemove(request.ToolExecutionId, out var pending))
                {
                    pending.CompletionSource.TrySetCanceled(cancellationToken);
                    DisposeRegistrations(pending);
                }
            });
        }

        timeoutRegistration = timeoutSource.Token.Register(() => Expire(request.ToolExecutionId));

        var pendingApproval = new PendingApproval(
            request,
            completionSource,
            registration,
            timeoutSource,
            timeoutRegistration);
        if (!_pendingApprovals.TryAdd(request.ToolExecutionId, pendingApproval))
        {
            registration.Dispose();
            timeoutRegistration.Dispose();
            timeoutSource.Dispose();
            throw new InvalidOperationException($"Tool approval for '{request.ToolExecutionId}' is already pending.");
        }

        if (cancellationToken.IsCancellationRequested &&
            _pendingApprovals.TryRemove(request.ToolExecutionId, out var canceledPending))
        {
            DisposeRegistrations(canceledPending);
            canceledPending.CompletionSource.TrySetCanceled(cancellationToken);
            return canceledPending.CompletionSource.Task;
        }

        // A very short timeout can fire after the callback is registered but before the pending item is
        // inserted. The callback cannot remove an item that is not visible yet, so close that race here.
        if (timeoutSource.IsCancellationRequested)
        {
            Expire(request.ToolExecutionId);
            return completionSource.Task;
        }

        try
        {
            ApprovalRequested?.Invoke(request);
        }
        catch
        {
            TryResolve(request.ToolExecutionId, approved: false);
        }

        return AwaitApprovalAsync(pendingApproval);
    }

    public bool TryResolve(Guid toolExecutionId, bool approved)
    {
        if (!_pendingApprovals.TryRemove(toolExecutionId, out var pending))
        {
            return false;
        }

        DisposeRegistrations(pending);
        return pending.CompletionSource.TrySetResult(approved);
    }

    public void RejectAll()
    {
        foreach (var toolExecutionId in _pendingApprovals.Keys)
        {
            TryResolve(toolExecutionId, approved: false);
        }
    }

    private void Expire(Guid toolExecutionId)
    {
        if (!_pendingApprovals.TryRemove(toolExecutionId, out var pending))
        {
            return;
        }

        pending.CompletionSource.TrySetResult(false);
        DisposeRegistrations(pending);
        try
        {
            ApprovalExpired?.Invoke(pending.Request);
        }
        catch
        {
        }
    }

    private static async Task<bool> AwaitApprovalAsync(PendingApproval pendingApproval)
    {
        try
        {
            return await pendingApproval.CompletionSource.Task;
        }
        finally
        {
            DisposeRegistrations(pendingApproval);
        }
    }

    private static void DisposeRegistrations(PendingApproval pending)
    {
        pending.CancellationRegistration.Dispose();
        pending.TimeoutRegistration.Dispose();
        pending.TimeoutSource.Dispose();
    }

    private sealed record PendingApproval(
        ToolApprovalRequest Request,
        TaskCompletionSource<bool> CompletionSource,
        CancellationTokenRegistration CancellationRegistration,
        CancellationTokenSource TimeoutSource,
        CancellationTokenRegistration TimeoutRegistration);
}
