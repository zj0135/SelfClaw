using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The single tool-invocation seam for a Direct turn: it owns approval, the durable execution checkpoint
/// and the turn's consecutive-fault budget. The explicit loop calls it once per call.
/// </summary>
internal sealed class DirectToolInvoker
{
    internal const string DeniedResult = "User denied this tool call.";

    /// <summary>
    /// The model-visible reason for a call the invoker refused to start because a steer batch was
    /// durably claimed first. It is distinct from a user denial so transcripts can tell
    /// an adjustment from a rejection, and it never counts as a tool fault.
    /// </summary>
    internal const string SupersededBySteerReason = "SupersededBySteer";

    /// <summary>
    /// How many consecutive tool faults are converted into a model-visible failure before the next one
    /// aborts the turn. An uncaught fault ends the explicit loop: a permanently broken tool costs
    /// at most this many attempts plus one
    /// instead of the whole tool-call budget.
    /// </summary>
    internal const int MaximumConsecutiveToolFaults = 2;

    private readonly DirectChatTurnRequest _request;
    private readonly IReadOnlyDictionary<string, DirectToolBinding> _bindings;
    private readonly IDirectTurnSteering? _steering;
    private int _consecutiveToolFaults;

    public DirectToolInvoker(
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, DirectToolBinding> bindings)
    {
        _request = request;
        _bindings = bindings;
        _steering = request.Steering;
    }

    public async ValueTask<object?> InvokeAsync(
        FunctionCallContent callContent,
        int requestIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callContent);
        cancellationToken.ThrowIfCancellationRequested();
        var toolName = callContent.Name;
        if (!_bindings.TryGetValue(toolName, out var binding))
        {
            throw new InvalidOperationException($"Direct tool '{toolName}' is not bound to this turn.");
        }

        // Pending phase: a steer claimed before this call starts skips the call outright.
        if (_steering?.HasSupersedingSteer == true)
        {
            return SupersededBySteerResult();
        }

        var arguments = new AIFunctionArguments(callContent.Arguments ?? new Dictionary<string, object?>());
        DirectToolResult? result = null;
        object? raw = null;
        Exception? fault = null;
        var superseded = false;
        var needsApproval =
            binding.RequiresApproval && _request.ToolPermissionMode != ToolPermissionMode.FullAccess;
        var decision = needsApproval
            ? await RequestApprovalAsync(binding, arguments, cancellationToken).ConfigureAwait(false)
            : ToolApprovalDecision.Approved;
        if (decision == ToolApprovalDecision.Denied)
        {
            result = new DirectToolResult(
                ToolCallStatus.Canceled,
                DeniedResult,
                JsonSerializer.SerializeToElement(DeniedResult),
                DeniedResult);
        }
        else if (decision == ToolApprovalDecision.Superseded)
        {
            superseded = true;
            result = SupersededBySteerResult();
        }
        else if (_steering is not null &&
                 !await _steering.TryBeginToolExecutionAsync(cancellationToken).ConfigureAwait(false))
        {
            // ReadyToExecute lost to a steer claim: the checkpoint and the tool never run, so the
            // permit stays irrevocable once granted.
            superseded = true;
            result = SupersededBySteerResult();
        }
        else
        {
            if (_request.ToolExecutionCheckpoint is not null)
            {
                await _request.ToolExecutionCheckpoint.BeforeExecutionAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                raw = await binding.Tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A faulting tool is a failure the model must see, not a turn failure: the real error
                // reaches it as a result, so the model can correct itself. Detail carries the message
                // rather than the stack trace because it is also persisted as the tool result and replayed
                // on later turns, where a stack trace is permanent prompt noise that leaks local paths.
                fault = exception;
                result = new DirectToolResult(
                    ToolCallStatus.Failed,
                    exception.Message,
                    JsonSerializer.SerializeToElement(new
                    {
                        type = exception.GetType().Name,
                        message = exception.Message
                    }),
                    exception.Message);
            }

            result ??= raw as DirectToolResult;
        }

        if (fault is null)
        {
            // A steer skip neither increments nor resets the consecutive-fault budget.
            if (!superseded) _consecutiveToolFaults = 0;
        }
        else
        {
            var consecutiveFaults = ++_consecutiveToolFaults;
            if (consecutiveFaults > MaximumConsecutiveToolFaults)
            {
                // The explicit loop terminates on this exception; it never retries the broken tool.
                throw new DirectToolFaultLimitException(binding.Tool.Name, consecutiveFaults, fault);
            }
        }

        return result is not null ? result : raw;
    }

    private async Task<ToolApprovalDecision> RequestApprovalAsync(
        DirectToolBinding binding,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var handler = _request.ToolApprovalHandler;
        if (handler is null)
        {
            return ToolApprovalDecision.Denied;
        }

        var approvalId = Guid.NewGuid();
        var approval = handler.RequestApprovalAsync(
                new ToolApprovalRequest(
                    approvalId,
                    binding.Tool.Name,
                    binding.Descriptor.DisplayName ?? binding.Tool.Name,
                    binding.Tool.Description,
                    JsonSerializer.Serialize(arguments, binding.Tool.JsonSerializerOptions),
                    _request.ConversationId,
                    binding.Descriptor.SourceKind ?? ToolSourceKind.BuiltIn,
                    binding.Descriptor.SourceId,
                    binding.TransportSummary,
                    binding.AnnotationsJson),
                cancellationToken);
        if (_steering is null)
        {
            return await approval.ConfigureAwait(false);
        }

        while (true)
        {
            var winner = await Task.WhenAny(approval, _steering.WaitForSteerPulseAsync(cancellationToken)).ConfigureAwait(false);
            if (winner == approval)
            {
                return await approval.ConfigureAwait(false);
            }

            if (await _steering.TryBeginToolExecutionAsync(cancellationToken).ConfigureAwait(false))
            {
                // The claim failed, so nothing may be revoked on behalf of an unconfirmed input.
                continue;
            }

            // The steer claim is durable: revoke a still-pending approval. If the user decided first
            // that decision stays the winner, but the claimed steer still wins execution admission.
            var revoked = handler.TryResolve(approvalId, ToolApprovalDecision.Superseded);
            var decided = await approval.ConfigureAwait(false);
            return revoked || decided != ToolApprovalDecision.Denied
                ? ToolApprovalDecision.Superseded : ToolApprovalDecision.Denied;
        }
    }

    private static DirectToolResult SupersededBySteerResult()
        => new(
            ToolCallStatus.Canceled,
            "Skipped because the user adjusted the current task.",
            JsonSerializer.SerializeToElement(new { reason = SupersededBySteerReason }),
            SupersededBySteerReason);
}
