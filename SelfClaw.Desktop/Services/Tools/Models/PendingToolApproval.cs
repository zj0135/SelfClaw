using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Tools.Models;

internal sealed record PendingToolApproval(
    ToolApprovalRequest Request,
    TaskCompletionSource<bool> CompletionSource,
    CancellationTokenRegistration CancellationRegistration,
    CancellationTokenSource TimeoutSource,
    CancellationTokenRegistration TimeoutRegistration);
