using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Tools.Models;

internal sealed record PendingToolApproval(
    ToolApprovalRequest Request,
    TaskCompletionSource<ToolApprovalDecision> CompletionSource,
    CancellationTokenRegistration CancellationRegistration);
