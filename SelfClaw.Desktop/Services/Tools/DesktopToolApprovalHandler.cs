using SelfClaw.Core.Interfaces;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Tools.Models;

namespace SelfClaw.Desktop.Services.Tools;

public sealed class DesktopToolApprovalHandler : IToolApprovalHandler
{
    private static readonly TimeSpan DefaultApprovalTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, PendingToolApproval> _pendingApprovals = new();
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

    /// <summary>
    /// Raised whenever a pending approval leaves the queue for any reason (resolved, cancelled,
    /// timed out, or rejected in bulk). Lets the UI advance a single-slot approval indicator without
    /// caring how the request was closed.
    /// </summary>
    public event Action<Guid>? ApprovalCompleted;

    internal IReadOnlyList<ToolApprovalRequest> GetPendingRequests()
        => _pendingApprovals.Values.Select(pending => pending.Request).ToImmutableArray();

    public Task<bool> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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
                    RaiseApprovalCompleted(request.ToolExecutionId);
                }
            });
        }

        timeoutRegistration = timeoutSource.Token.Register(() => Expire(request.ToolExecutionId));

        var pendingApproval = new PendingToolApproval(
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
        var resolved = pending.CompletionSource.TrySetResult(approved);
        RaiseApprovalCompleted(toolExecutionId);
        return resolved;
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
        RaiseApprovalCompleted(toolExecutionId);
        try
        {
            ApprovalExpired?.Invoke(pending.Request);
        }
        catch
        {
        }
    }

    private void RaiseApprovalCompleted(Guid toolExecutionId)
    {
        try
        {
            ApprovalCompleted?.Invoke(toolExecutionId);
        }
        catch
        {
        }
    }

    private static async Task<bool> AwaitApprovalAsync(PendingToolApproval pendingApproval)
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

    private static void DisposeRegistrations(PendingToolApproval pending)
    {
        pending.CancellationRegistration.Dispose();
        pending.TimeoutRegistration.Dispose();
        pending.TimeoutSource.Dispose();
    }

}
