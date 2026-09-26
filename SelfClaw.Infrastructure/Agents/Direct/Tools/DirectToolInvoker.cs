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
/// The single tool-invocation seam for a Direct turn: it owns the tool hooks, approval and the durable
/// execution checkpoint, and it is installed as the pipeline's M.E.AI <c>FunctionInvoker</c> so every
/// tool call passes through exactly once.
/// </summary>
internal sealed class DirectToolInvoker
{
    internal const string DeniedResult = "User denied this tool call.";

    private readonly DirectChatTurnRequest _request;
    private readonly IReadOnlyDictionary<string, DirectToolBinding> _bindings;
    private readonly DirectTurnHooks _hooks;
    private readonly ConcurrentDictionary<string, ToolHookOutcome> _outcomes = new(StringComparer.Ordinal);
    private int _callCount;

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
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var toolName = context.Function.Name;
        if (!_bindings.TryGetValue(toolName, out var binding))
        {
            throw new InvalidOperationException($"Direct tool '{toolName}' is not bound to this turn.");
        }

        Interlocked.Increment(ref _callCount);
        var callId = context.CallContent.CallId;
        var stopwatch = Stopwatch.StartNew();
        var call = new ToolHookCall(
            callId,
            context.Iteration,
            binding,
            context.Arguments,
            _request.ToolPermissionMode);
        var pre = await _hooks.ToolExecutingAsync(call, cancellationToken).ConfigureAwait(false);
        DirectToolResult? result = null;
        object? raw = null;
        string? deniedBy = null;
        if (pre.BlockedBy is not null)
        {
            deniedBy = "hook";
            var message = pre.BlockReason ?? HookNotes.Blocked(pre.BlockedBy, null);
            result = new DirectToolResult(
                ToolCallStatus.Blocked, message, JsonSerializer.SerializeToElement(message), message);
        }
        else
        {
            var arguments = pre.EffectiveArguments ?? context.Arguments;
            var needsApproval =
                binding.RequiresApproval && _request.ToolPermissionMode != ToolPermissionMode.FullAccess ||
                pre.ApprovalRequiredBy.Count > 0;
            if (needsApproval && !await RequestApprovalAsync(context, binding, arguments, pre, cancellationToken)
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
                    raw = await context.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The result object does not exist on this path, so the side-band outcome is the only
                    // place a rewrite or an ask can still be recorded for the transcript.
                    var threw = new ToolHookResult(
                        ToolCallStatus.Failed,
                        DeniedBy: null,
                        Summary: null,
                        Content: null,
                        Error: exception.Message,
                        EffectiveArgumentsJson: pre.EffectiveArgumentsJson,
                        Duration: stopwatch.Elapsed);
                    var postThrew = await _hooks.ToolExecutedAsync(call, threw, cancellationToken).ConfigureAwait(false);
                    RecordOutcome(callId, pre, postThrew);
                    throw;
                }

                result = raw as DirectToolResult;
            }
        }

        var hookResult = result is not null
            ? new ToolHookResult(
                result.Status,
                deniedBy,
                result.Summary,
                result.Content,
                Error: null,
                pre.EffectiveArgumentsJson,
                stopwatch.Elapsed)
            : new ToolHookResult(
                ToolCallStatus.Completed,
                deniedBy,
                Summary: null,
                Content: null,
                Error: null,
                pre.EffectiveArgumentsJson,
                stopwatch.Elapsed);
        var post = await _hooks.ToolExecutedAsync(call, hookResult, cancellationToken).ConfigureAwait(false);
        RecordOutcome(callId, pre, post);
        return result is not null ? AttachFeedback(result, pre, post) : raw;
    }

    /// <summary>
    /// Removes and returns the hook outcome recorded for a call. The turn translator calls this for
    /// every translated <c>FunctionResultContent</c>, including exception results.
    /// </summary>
    public ToolHookOutcome? TryTakeOutcome(string callId)
        => _outcomes.TryRemove(callId, out var outcome) ? outcome : null;

    private async Task<bool> RequestApprovalAsync(
        FunctionInvocationContext context,
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
                    JsonSerializer.Serialize(arguments, context.Function.JsonSerializerOptions),
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
