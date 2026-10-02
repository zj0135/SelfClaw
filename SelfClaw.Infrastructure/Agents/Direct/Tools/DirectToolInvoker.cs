using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The single tool-invocation seam for a Direct turn: it owns the tool hooks, approval, the durable
/// execution checkpoint and the turn's consecutive-fault budget. The explicit loop calls it once per call.
/// </summary>
internal sealed class DirectToolInvoker
{
    internal const string DeniedResult = "User denied this tool call.";

    /// <summary>
    /// How many consecutive tool faults are converted into a model-visible failure before the next one
    /// aborts the turn. An uncaught fault ends the explicit loop: a permanently broken tool costs
    /// at most this many attempts plus one
    /// instead of the whole tool-call budget.
    /// </summary>
    internal const int MaximumConsecutiveToolFaults = 2;

    private readonly DirectChatTurnRequest _request;
    private readonly IReadOnlyDictionary<string, DirectToolBinding> _bindings;
    private readonly DirectTurnHooks _hooks;
    private readonly ConcurrentDictionary<string, ToolHookOutcome> _outcomes = new(StringComparer.Ordinal);
    private int _callCount;
    private int _consecutiveToolFaults;

    public DirectToolInvoker(
        DirectChatTurnRequest request,
        IReadOnlyDictionary<string, DirectToolBinding> bindings,
        DirectTurnHooks hooks)
    {
        _request = request;
        _bindings = bindings;
        _hooks = hooks;
    }

    public int CallCount => Volatile.Read(ref _callCount);

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

        Interlocked.Increment(ref _callCount);
        var callId = callContent.CallId;
        var originalArguments = new AIFunctionArguments(callContent.Arguments ?? new Dictionary<string, object?>());
        var stopwatch = Stopwatch.StartNew();
        var call = new ToolHookCall(
            callId,
            requestIndex,
            binding,
            originalArguments,
            _request.ToolPermissionMode);
        var pre = await _hooks.ToolExecutingAsync(call, cancellationToken).ConfigureAwait(false);
        DirectToolResult? result = null;
        object? raw = null;
        string? deniedBy = null;
        Exception? fault = null;
        if (pre.BlockedBy is not null)
        {
            deniedBy = "hook";
            var message = pre.BlockReason ?? HookNotes.Blocked(pre.BlockedBy, null);
            result = new DirectToolResult(
                ToolCallStatus.Blocked, message, JsonSerializer.SerializeToElement(message), message);
        }
        else
        {
            var arguments = pre.EffectiveArguments ?? originalArguments;
            var needsApproval =
                (binding.RequiresApproval && _request.ToolPermissionMode != ToolPermissionMode.FullAccess) ||
                pre.ApprovalRequiredBy.Count > 0;
            if (needsApproval && !await RequestApprovalAsync(binding, arguments, pre, cancellationToken)
                    .ConfigureAwait(false))
            {
                deniedBy = "user";
                result = new DirectToolResult(
                    ToolCallStatus.Canceled,
                    DeniedResult,
                    JsonSerializer.SerializeToElement(DeniedResult),
                    DeniedResult);
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
                    // reaches it as a result, so the model can correct itself. Fault metadata stays in the
                    // hook outcome. Detail carries the message rather than the stack trace because it is
                    // also persisted as the tool result and replayed on later turns, where a stack trace
                    // is permanent prompt noise that leaks local paths.
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
        }

        // A faulted call keeps reporting Error with no summary or content, so what plugins observe through
        // toolExecuted is unchanged; only the returned result differs.
        var hookResult = fault is not null
            ? new ToolHookResult(
                ToolCallStatus.Failed,
                deniedBy,
                Summary: null,
                Content: null,
                Error: fault.Message,
                EffectiveArgumentsJson: pre.EffectiveArgumentsJson,
                Duration: stopwatch.Elapsed)
            : new ToolHookResult(
                result?.Status ?? ToolCallStatus.Completed,
                deniedBy,
                Summary: result?.Summary,
                Content: result?.Content,
                Error: null,
                EffectiveArgumentsJson: pre.EffectiveArgumentsJson,
                Duration: stopwatch.Elapsed);
        var post = await _hooks.ToolExecutedAsync(call, hookResult, cancellationToken).ConfigureAwait(false);
        RecordOutcome(callId, pre, post);

        if (fault is null)
        {
            Interlocked.Exchange(ref _consecutiveToolFaults, 0);
        }
        else
        {
            var consecutiveFaults = Interlocked.Increment(ref _consecutiveToolFaults);
            if (consecutiveFaults > MaximumConsecutiveToolFaults)
            {
                // The explicit loop terminates on this exception; it never retries the broken tool.
                throw new DirectToolFaultLimitException(binding.Tool.Name, consecutiveFaults, fault);
            }
        }

        return result is not null ? AttachFeedback(result, pre, post) : raw;
    }

    /// <summary>
    /// Removes and returns the hook outcome recorded for a call. The turn translator calls this for
    /// every translated <c>FunctionResultContent</c>, including exception results.
    /// </summary>
    public ToolHookOutcome? TryTakeOutcome(string callId)
        => _outcomes.TryRemove(callId, out var outcome) ? outcome : null;

    private async Task<bool> RequestApprovalAsync(
        DirectToolBinding binding,
        AIFunctionArguments arguments,
        ToolExecutingOutcome pre,
        CancellationToken cancellationToken)
    {
        if (_request.ToolApprovalHandler is null)
        {
            return false;
        }

        return await _request.ToolApprovalHandler.RequestApprovalAsync(
                new ToolApprovalRequest(
                    Guid.NewGuid(),
                    binding.Tool.Name,
                    binding.Descriptor.DisplayName ?? binding.Tool.Name,
                    binding.Tool.Description,
                    JsonSerializer.Serialize(arguments, binding.Tool.JsonSerializerOptions),
                    _request.ConversationId,
                    binding.Descriptor.SourceKind ?? ToolSourceKind.BuiltIn,
                    binding.Descriptor.SourceId,
                    binding.TransportSummary,
                    binding.AnnotationsJson,
                    pre.ApprovalRequiredBy,
                    pre.ApprovalReason,
                    pre.ArgumentsModifiedBy),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void RecordOutcome(string callId, ToolExecutingOutcome pre, ToolExecutedOutcome post)
    {
        var failures = pre.IgnoredFailures.Concat(post.IgnoredFailures).ToArray();
        if (failures.Length == 0 &&
            post.Feedback.Count == 0 &&
            pre.ArgumentsModifiedBy.Count == 0 &&
            pre.ApprovalRequiredBy.Count == 0 &&
            pre.BlockedBy is null)
        {
            return;
        }

        _outcomes[callId] = new ToolHookOutcome(
            pre.EffectiveArgumentsJson,
            pre.ArgumentsModifiedBy,
            pre.ApprovalRequiredBy,
            pre.BlockedBy,
            pre.BlockReason,
            post.Feedback,
            failures);
    }

    private static DirectToolResult AttachFeedback(
        DirectToolResult result,
        ToolExecutingOutcome pre,
        ToolExecutedOutcome post)
    {
        var feedback = new List<DirectToolHookFeedback>();
        if (pre.EffectiveArgumentsJson is not null && pre.ArgumentsModifiedBy.Count > 0)
        {
            feedback.Add(new DirectToolHookFeedback(
                "selfclaw",
                HookNotes.ArgumentsModified(pre.EffectiveArgumentsJson, pre.ArgumentsModifiedBy)));
        }

        feedback.AddRange(post.Feedback.Select(item =>
            new DirectToolHookFeedback(HookNotes.Describe(item.Source), item.Text)));
        return feedback.Count == 0 ? result : result with { HookFeedback = feedback };
    }
}
