using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
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

    public async ValueTask<DirectToolResult> InvokeAsync(
        FunctionCallContent callContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callContent);
        cancellationToken.ThrowIfCancellationRequested();
        // Pending phase: a steer claimed before this call starts skips the call outright. It is judged before
        // the call's own validity because the whole batch is being abandoned: a hallucinated name inside it
        // must not spend the fault budget and turn "the user adjusted the task" into a failed turn.
        if (_steering?.HasSupersedingSteer == true)
        {
            return SupersededBySteerResult();
        }

        // A hallucinated tool name or unparseable arguments is a model-side mistake, not a turn failure: it
        // reaches the model as a Failed result and spends the same consecutive-fault budget as a faulting
        // tool, so the model can correct itself. Only a repeated call id (a provider contract breach) and a
        // checkpoint failure still end the turn. The call's ToolCallStartedEvent is already published, so the
        // result returned here is its one and only terminal event.
        if (string.IsNullOrWhiteSpace(callContent.Name) || !_bindings.TryGetValue(callContent.Name, out var binding))
        {
            return RejectCall(callContent, $"Direct tool '{callContent.Name}' is not bound to this turn.");
        }

        if (callContent.Exception is { } argumentFault)
        {
            return RejectCall(callContent,
                $"The arguments for Direct tool '{callContent.Name}' could not be parsed: {argumentFault.Message}");
        }

        var arguments = new AIFunctionArguments(callContent.Arguments ?? new Dictionary<string, object?>());
        var (refusal, isUserDenial) = await ResolveGateAsync(binding, arguments, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            // A user denial is a decision, not a fault: it closes the streak like a clean execution.
            if (isUserDenial)
            {
                _consecutiveToolFaults = 0;
            }

            return refusal;
        }

        return await ExecuteAsync(binding, arguments, cancellationToken).ConfigureAwait(false);
    }

    // A null refusal means "execute". Denied and Superseded carry the model-visible result to report instead
    // of executing; only a denial closes the consecutive-fault streak.
    private async Task<(DirectToolResult? Refusal, bool IsUserDenial)> ResolveGateAsync(
        DirectToolBinding binding, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var needsApproval =
            binding.RequiresApproval && _request.ToolPermissionMode != ToolPermissionMode.FullAccess;
        var decision = needsApproval
            ? await RequestApprovalAsync(binding, arguments, cancellationToken).ConfigureAwait(false)
            : ToolApprovalDecision.Approved;
        if (decision == ToolApprovalDecision.Denied)
        {
            return (DeniedByUserResult(), true);
        }

        if (decision == ToolApprovalDecision.Superseded)
        {
            return (SupersededBySteerResult(), false);
        }

        if (_steering is not null &&
            !await _steering.TryBeginToolExecutionAsync(cancellationToken).ConfigureAwait(false))
        {
            // ReadyToExecute lost to a steer claim: the checkpoint and the tool never run, so the
            // permit stays irrevocable once granted.
            return (SupersededBySteerResult(), false);
        }

        return (null, false);
    }

    private async ValueTask<DirectToolResult> ExecuteAsync(
        DirectToolBinding binding, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_request.ToolExecutionCheckpoint is not null)
        {
            await _request.ToolExecutionCheckpoint.BeforeExecutionAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var raw = await binding.Tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            _consecutiveToolFaults = 0;
            return BoundPayload(raw as DirectToolResult
                ?? throw new InvalidOperationException(
                    $"Tool '{binding.Tool.Name}' returned an invalid result."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A faulting tool is a failure the model must see, not a turn failure: the real error
            // reaches it as a result, so the model can correct itself. Detail carries the message
            // rather than the stack trace because it is also persisted as the tool result and replayed
            // on later turns, where a stack trace is permanent prompt noise that leaks local paths.
            RecordConsecutiveFault(binding.Tool.Name, exception);
            return new DirectToolResult(
                ToolCallStatus.Failed,
                exception.Message,
                JsonSerializer.SerializeToElement(new
                {
                    type = exception.GetType().Name,
                    message = exception.Message
                }),
                exception.Message);
        }
    }

    private void RecordConsecutiveFault(string toolName, Exception fault)
    {
        var consecutiveFaults = ++_consecutiveToolFaults;
        if (consecutiveFaults > MaximumConsecutiveToolFaults)
        {
            // The explicit loop terminates on this exception; it never retries the broken tool.
            throw new DirectToolFaultLimitException(toolName, consecutiveFaults, fault);
        }
    }

    /// <summary>
    /// The model-visible answer for a call that never reached a tool: the same shape a faulting tool
    /// produces, so the model can read the error and correct itself.
    /// </summary>
    private DirectToolResult RejectCall(FunctionCallContent call, string message)
    {
        var fault = new InvalidOperationException(message);
        RecordConsecutiveFault(call.Name ?? string.Empty, fault);
        return new DirectToolResult(
            ToolCallStatus.Failed,
            message,
            JsonSerializer.SerializeToElement(new { type = "InvalidToolCall", message }),
            message);
    }

    /// <summary>
    /// Applies the tool-result text limit here, at the one seam every Direct tool result passes through, so the
    /// turn's context, the transcript event and the stored row all carry the same text. Limiting only on the way
    /// to storage would let the model read more in this turn than the next one can replay, which shows up as a
    /// context size that never comes back. The structured payload is not touched: every tool that can grow one
    /// bounds it where it is built (search budgets its result, reads page, shell output is capped), so the text
    /// and the payload stay the same size by construction instead of being cut into a different shape here.
    /// </summary>
    private static DirectToolResult BoundPayload(DirectToolResult result)
        => result.Detail is { } detail && detail.Length > ToolResultTextLimits.MaximumCharacters
            ? result with { Detail = ToolResultTextLimits.Limit(detail) }
            : result;

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
                    DirectToolSource.ResolveId(binding.Descriptor, arguments),
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

    private static DirectToolResult DeniedByUserResult()
        => new(
            ToolCallStatus.Canceled,
            DeniedResult,
            JsonSerializer.SerializeToElement(DeniedResult),
            DeniedResult);

    private static DirectToolResult SupersededBySteerResult()
        => new(
            ToolCallStatus.Canceled,
            "Skipped because the user adjusted the current task.",
            JsonSerializer.SerializeToElement(new { reason = SupersededBySteerReason }),
            SupersededBySteerReason);
}
