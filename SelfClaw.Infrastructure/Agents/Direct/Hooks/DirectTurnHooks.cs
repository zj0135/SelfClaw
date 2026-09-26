using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Processes;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// One turn's effective hook set. It owns the per-event ordering, matcher evaluation, decision
/// merging, failure policy and execution log for the six hook events; the process runner and the
/// async executor are injected so the merge logic stays testable without real processes.
/// </summary>
internal sealed class DirectTurnHooks
{
    private const int SchemaVersion = 1;
    private const int MaximumRunStartingContextBytes = 64 * 1024;
    private const int MaximumContextSectionBytes = 16 * 1024;
    private const int MaximumFeedbackBytes = 8 * 1024;
    private const int MaximumCallFeedbackBytes = 16 * 1024;
    private const int MaximumHeadersPerHook = 16;

    private static readonly HashSet<string> RunStartingDecisions = ["continue", "block"];
    private static readonly HashSet<string> ToolExecutingDecisions = ["continue", "deny", "ask"];

    private readonly DirectHookTurnContext _context;
    private readonly IReadOnlyList<ResolvedPluginHook> _runStarting;
    private readonly IReadOnlyList<ResolvedPluginHook> _runCompleted;
    private readonly IReadOnlyList<ResolvedPluginHook> _toolExecuting;
    private readonly IReadOnlyList<ResolvedPluginHook> _toolExecuted;
    private readonly IReadOnlyList<ResolvedPluginHook> _httpRequestSending;
    private readonly IReadOnlyList<ResolvedPluginHook> _httpResponseReceived;
    private readonly Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> _runner;
    private readonly Func<AsyncHookWork, bool> _enqueue;
    private readonly PluginHookExecutionLog _log;
    private readonly ILogger _logger;
    private int _runStartingStarted;
    private int _runCompletedDelivered;

    public DirectTurnHooks(
        DirectHookTurnContext context,
        IReadOnlyList<ResolvedPluginHook> hooks,
        Func<HookProcessStart, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<HookProcessResult>> runner,
        Func<AsyncHookWork, bool> enqueue,
        PluginHookExecutionLog log,
        ILogger? logger = null)
    {
        _context = context;
        _runStarting = Filter(hooks, PluginHookEvent.RunStarting);
        _runCompleted = Filter(hooks, PluginHookEvent.RunCompleted);
        _toolExecuting = Filter(hooks, PluginHookEvent.ToolExecuting);
        _toolExecuted = Filter(hooks, PluginHookEvent.ToolExecuted);
        _httpRequestSending = Filter(hooks, PluginHookEvent.HttpRequestSending);
        _httpResponseReceived = Filter(hooks, PluginHookEvent.HttpResponseReceived);
        _runner = runner;
        _enqueue = enqueue;
        _log = log;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool HasHttpHooks => _httpRequestSending.Count > 0 || _httpResponseReceived.Count > 0;

    public bool HasRunStartingHooks => _runStarting.Count > 0;

    public bool WantsRequestBody(string host)
        => _httpRequestSending.Any(hook =>
            hook.Contribution.IncludeRequestBody &&
            HookMatcher.MatchesHttp(hook.Contribution.Matcher, _context.Origin, host));

    public async Task<RunStartingOutcome> RunStartingAsync(
        RunStartingInput input,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _runStartingStarted, 1);
        var sections = new List<HookContextSection>();
        var notices = new List<string>();
        var totalBytes = 0;
        HookSource? blockedBy = null;
        string? blockReason = null;
        foreach (var hook in _runStarting)
        {
            if (!HookMatcher.MatchesTurn(hook.Contribution.Matcher, _context.Origin))
            {
                continue;
            }

            var source = Source(hook);
            var execution = await ExecuteSyncAsync<RunStartingDecision>(
                    hook,
                    "runStarting",
                    HookProtocol.Serialize(BuildRunStartingPayload(hook, input)),
                    RunStartingDecisions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.FailureKind is not null)
            {
                RecordFailure(hook, "runStarting", execution);
                if (hook.Contribution.OnFailure == PluginHookFailurePolicy.Block)
                {
                    blockedBy = source;
                    blockReason = HookNotes.BlockedByFailure(source, execution.FailureKind, execution.FailureDetail);
                    break;
                }

                notices.Add(HookNotes.FailureIgnored(source, execution.FailureKind));
                continue;
            }

            var decision = execution.Parse!.Decision;
            if (decision?.Decision == "block")
            {
                blockedBy = source;
                blockReason = HookNotes.Blocked(source, decision.Reason);
                RecordExecution(hook, "runStarting", "blocked", blockReason, execution.Process);
                break;
            }

            if (string.IsNullOrEmpty(decision?.AdditionalContext))
            {
                RecordExecution(hook, "runStarting", "continued", null, execution.Process);
                continue;
            }

            var (text, truncated) = HookProtocol.Truncate(decision.AdditionalContext, MaximumContextSectionBytes);
            if (truncated)
            {
                RecordExecution(hook, "runStarting", "failed", "limitExceeded", execution.Process);
                if (hook.Contribution.OnFailure == PluginHookFailurePolicy.Block)
                {
                    blockedBy = source;
                    blockReason = HookNotes.BlockedByFailure(source, "limitExceeded", null);
                    break;
                }

                notices.Add(HookNotes.FailureIgnored(source, "limitExceeded"));
                continue;
            }

            var byteCount = Encoding.UTF8.GetByteCount(text);
            if (totalBytes + byteCount > MaximumRunStartingContextBytes)
            {
                RecordExecution(hook, "runStarting", "failed", "contextLimitExceeded", execution.Process);
                notices.Add(HookNotes.ContextDropped(source));
                continue;
            }

            totalBytes += byteCount;
            sections.Add(new HookContextSection(source, text));
            notices.Add(HookNotes.ContextAdded(source, text.Length));
            RecordExecution(hook, "runStarting", "contextAdded", null, execution.Process);
        }

        return new RunStartingOutcome(blockedBy, blockReason, sections, notices);
    }

    public void RunCompleted(RunCompletedInput input)
    {
        if (Volatile.Read(ref _runStartingStarted) == 0 ||
            Interlocked.Exchange(ref _runCompletedDelivered, 1) != 0)
        {
            return;
        }

        foreach (var hook in _runCompleted)
        {
            if (!HookMatcher.MatchesTurn(hook.Contribution.Matcher, _context.Origin))
            {
                continue;
            }

            EnqueueAsync(hook, "runCompleted", HookProtocol.Serialize(BuildRunCompletedPayload(hook, input)));
        }
    }

    public async Task<ToolExecutingOutcome> ToolExecutingAsync(
        ToolHookCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var descriptor = call.Binding.Descriptor;
        var currentArguments = call.Arguments;
        var (currentArgumentsJson, currentTruncated) = TruncateArguments(currentArguments);
        var modifiers = new List<HookSource>();
        var approvalRequiredBy = new List<HookSource>();
        var failures = new List<HookFailureNotice>();
        string? approvalReason = null;
        HookSource? blockedBy = null;
        string? blockReason = null;
        foreach (var hook in _toolExecuting)
        {
            if (!HookMatcher.MatchesTool(
                    hook.Contribution.Matcher,
                    _context.Origin,
                    descriptor.ProviderName,
                    descriptor.SourceId,
                    descriptor.Kind,
                    descriptor.SourceKind ?? ToolSourceKind.BuiltIn))
            {
                continue;
            }

            var source = Source(hook);
            var execution = await ExecuteSyncAsync<ToolExecutingDecision>(
                    hook,
                    "toolExecuting",
                    HookProtocol.Serialize(BuildToolExecutingPayload(
                        hook, call, currentArgumentsJson, currentTruncated)),
                    ToolExecutingDecisions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.FailureKind is not null)
            {
                RecordFailure(hook, "toolExecuting", execution);
                if (hook.Contribution.OnFailure == PluginHookFailurePolicy.Block)
                {
                    blockedBy = source;
                    blockReason = HookNotes.BlockedByFailure(source, execution.FailureKind, execution.FailureDetail);
                    break;
                }

                failures.Add(new HookFailureNotice(source, execution.FailureKind, execution.FailureDetail ?? string.Empty));
                continue;
            }

            var decision = execution.Parse!.Decision;
            if (decision?.Decision == "deny")
            {
                blockedBy = source;
                blockReason = HookNotes.Blocked(source, decision.Reason);
                RecordExecution(hook, "toolExecuting", "denied", blockReason, execution.Process);
                break;
            }

            var outcome = "continued";
            if (decision?.Decision == "ask")
            {
                approvalRequiredBy.Add(source);
                approvalReason ??= decision.Reason;
                outcome = "approvalRequired";
            }

            if (decision?.UpdatedArguments is JsonElement updatedArguments)
            {
                if (!HookArgumentsValidator.TryValidate(updatedArguments, call.Binding.Tool.JsonSchema, out var error))
                {
                    RecordExecution(hook, "toolExecuting", "failed", $"argumentsInvalid: {error}", execution.Process);
                    if (hook.Contribution.OnFailure == PluginHookFailurePolicy.Block)
                    {
                        blockedBy = source;
                        blockReason = HookNotes.BlockedByFailure(source, "argumentsInvalid", error);
                        break;
                    }

                    failures.Add(new HookFailureNotice(source, "argumentsInvalid", error ?? string.Empty));
                    continue;
                }

                currentArguments = CreateArguments(updatedArguments, call.Arguments);
                (currentArgumentsJson, currentTruncated) = TruncateArguments(currentArguments);
                modifiers.Add(source);
                outcome = "modified";
            }

            RecordExecution(hook, "toolExecuting", outcome, null, execution.Process);
        }

        return new ToolExecutingOutcome(
            modifiers.Count > 0 ? currentArguments : null,
            modifiers.Count > 0 ? SerializeArgumentsJson(currentArguments).GetRawText() : null,
            modifiers,
            approvalRequiredBy,
            approvalReason,
            blockedBy,
            blockReason,
            failures);
    }

    public async Task<ToolExecutedOutcome> ToolExecutedAsync(
        ToolHookCall call,
        ToolHookResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        var descriptor = call.Binding.Descriptor;
        var (originalArguments, originalTruncated) = TruncateArguments(call.Arguments);
        var effectiveArguments = ParseJson(result.EffectiveArgumentsJson);
        var feedback = new List<HookFeedback>();
        var failures = new List<HookFailureNotice>();
        var feedbackBytes = 0;
        foreach (var hook in _toolExecuted)
        {
            if (!HookMatcher.MatchesTool(
                    hook.Contribution.Matcher,
                    _context.Origin,
                    descriptor.ProviderName,
                    descriptor.SourceId,
                    descriptor.Kind,
                    descriptor.SourceKind ?? ToolSourceKind.BuiltIn))
            {
                continue;
            }

            var source = Source(hook);
            var payload = HookProtocol.Serialize(BuildToolExecutedPayload(
                hook, call, result, originalArguments, originalTruncated, effectiveArguments));
            if (hook.Contribution.RunAsync)
            {
                EnqueueAsync(hook, "toolExecuted", payload);
                continue;
            }

            var execution = await ExecuteSyncAsync<ToolExecutedDecision>(
                    hook, "toolExecuted", payload, [], cancellationToken)
                .ConfigureAwait(false);
            if (execution.FailureKind is not null)
            {
                RecordFailure(hook, "toolExecuted", execution);
                failures.Add(new HookFailureNotice(source, execution.FailureKind, execution.FailureDetail ?? string.Empty));
                continue;
            }

            var text = execution.Parse!.Decision?.Feedback;
            if (string.IsNullOrEmpty(text))
            {
                RecordExecution(hook, "toolExecuted", "observed", null, execution.Process);
                continue;
            }

            var (truncatedText, truncated) = HookProtocol.Truncate(text, MaximumFeedbackBytes);
            if (truncated)
            {
                RecordExecution(hook, "toolExecuted", "failed", "limitExceeded", execution.Process);
                failures.Add(new HookFailureNotice(source, "limitExceeded", "The hook feedback exceeded 8 KiB."));
                continue;
            }

            var byteCount = Encoding.UTF8.GetByteCount(truncatedText);
            if (feedbackBytes + byteCount > MaximumCallFeedbackBytes)
            {
                RecordExecution(hook, "toolExecuted", "failed", "limitExceeded", execution.Process);
                failures.Add(new HookFailureNotice(
                    source, "limitExceeded", "The turn's tool feedback total exceeded 16 KiB."));
                continue;
            }

            feedbackBytes += byteCount;
            feedback.Add(new HookFeedback(source, truncatedText));
            var feedbackDetail = result.Error is null ? null : "feedback ignored: tool threw";
            RecordExecution(hook, "toolExecuted", "feedback", feedbackDetail, execution.Process);
        }

        return new ToolExecutedOutcome(feedback, failures);
    }

    public async Task<HttpRequestSendingOutcome> HttpRequestSendingAsync(
        HttpHookRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Request.RequestUri?.Host ?? string.Empty;
        var headers = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hook in _httpRequestSending)
        {
            if (!HookMatcher.MatchesHttp(hook.Contribution.Matcher, _context.Origin, host))
            {
                continue;
            }

            var execution = await ExecuteSyncAsync<HttpRequestSendingDecision>(
                    hook,
                    "httpRequestSending",
                    HookProtocol.Serialize(BuildHttpRequestSendingPayload(hook, request)),
                    [],
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.FailureKind is not null)
            {
                RecordFailure(hook, "httpRequestSending", execution);
                continue;
            }

            var added = 0;
            foreach (var pair in execution.Parse!.Decision?.AddHeaders ?? new Dictionary<string, string>())
            {
                if (added >= MaximumHeadersPerHook)
                {
                    break;
                }

                if (!HookHeaderPolicy.IsAllowed(pair.Key, out var nameRejection))
                {
                    RecordExecution(hook, "httpRequestSending", "observed", pair.Key + ": " + nameRejection, execution.Process);
                    continue;
                }

                if (!HookHeaderPolicy.IsValidValue(pair.Value, out var valueRejection))
                {
                    RecordExecution(hook, "httpRequestSending", "observed", pair.Key + ": " + valueRejection, execution.Process);
                    continue;
                }

                if (!seen.Add(pair.Key))
                {
                    RecordExecution(hook, "httpRequestSending", "observed", pair.Key + ": already set", execution.Process);
                    continue;
                }

                headers.Add(new KeyValuePair<string, string>(pair.Key, pair.Value));
                added++;
            }

            RecordExecution(
                hook,
                "httpRequestSending",
                added > 0 ? "headersAdded" : "continued",
                null,
                execution.Process);
        }

        return new HttpRequestSendingOutcome(headers);
    }

    public void HttpResponseReceived(HttpHookResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var host = response.Request.RequestUri?.Host ?? string.Empty;
        foreach (var hook in _httpResponseReceived)
        {
            if (!HookMatcher.MatchesHttp(hook.Contribution.Matcher, _context.Origin, host))
            {
                continue;
            }

            EnqueueAsync(hook, "httpResponseReceived", HookProtocol.Serialize(BuildHttpResponseReceivedPayload(hook, response)));
        }
    }

    private static IReadOnlyList<ResolvedPluginHook> Filter(
        IReadOnlyList<ResolvedPluginHook> hooks,
        PluginHookEvent hookEvent)
        => hooks.Where(hook => hook.Contribution.Event == hookEvent).ToArray();

    private async Task<HookExecution<TDecision>> ExecuteSyncAsync<TDecision>(
        ResolvedPluginHook hook,
        string eventName,
        byte[] payload,
        IReadOnlyCollection<string> allowedDecisions,
        CancellationToken cancellationToken)
        where TDecision : class
    {
        var start = ExpandStart(hook);
        if (start is null)
        {
            return new HookExecution<TDecision>(null, "templateUnavailable", null, null);
        }

        HookProcessResult result;
        try
        {
            result = await _runner(start, payload, hook.Contribution.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RecordExecution(hook, eventName, "cancelled", null, null);
            throw;
        }

        if (result.Exit != HookProcessExit.Exited)
        {
            var kind = result.Exit switch
            {
                HookProcessExit.TimedOut => "timedOut",
                HookProcessExit.OutputTooLarge => "outputTooLarge",
                HookProcessExit.LaunchFailed => "launchFailed",
                _ => "launchFailed"
            };
            return new HookExecution<TDecision>(null, kind, result.FailureDetail, result);
        }

        if (result.ExitCode != 0)
        {
            return new HookExecution<TDecision>(
                null, "nonZeroExit", $"Hook exited with code {result.ExitCode}.", result);
        }

        var parse = HookProtocol.Parse<TDecision>(result.Stdout, allowedDecisions);
        return parse.FailureKind is not null
            ? new HookExecution<TDecision>(null, parse.FailureKind, parse.FailureDetail, result)
            : new HookExecution<TDecision>(parse, null, null, result);
    }

    private void EnqueueAsync(ResolvedPluginHook hook, string eventName, byte[] payload)
    {
        var start = ExpandStart(hook);
        if (start is null)
        {
            RecordExecution(hook, eventName, "failed", "templateUnavailable", null);
            return;
        }

        _enqueue(new AsyncHookWork(hook, eventName, payload, _context.TurnId, start));
    }

    private HookProcessStart? ExpandStart(ResolvedPluginHook hook)
    {
        var command = PluginCommandTemplate.TryExpand(
            hook.Contribution.Command, _context.WorkspaceRoot, hook.PluginRoot);
        if (command is null)
        {
            return null;
        }

        var arguments = new List<string>(hook.Contribution.Arguments.Count);
        foreach (var argument in hook.Contribution.Arguments)
        {
            var expanded = PluginCommandTemplate.TryExpand(argument, _context.WorkspaceRoot, hook.PluginRoot);
            if (expanded is null)
            {
                return null;
            }

            arguments.Add(expanded);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SELFCLAW_HOOK_EVENT"] = EventName(hook.Contribution.Event),
            ["SELFCLAW_PLUGIN_ID"] = hook.PluginId,
            ["SELFCLAW_HOOK_ID"] = hook.Contribution.Id,
            ["SELFCLAW_PLUGIN_ROOT"] = hook.PluginRoot
        };
        if (_context.WorkspaceRoot is not null)
        {
            environment["SELFCLAW_WORKSPACE_ROOT"] = _context.WorkspaceRoot;
        }

        // The version directory itself is read-only: a reinspection of identical content reuses it,
        // and a running process would otherwise lock it. Use the workspace (or temp) instead.
        return new HookProcessStart(
            command,
            arguments,
            _context.WorkspaceRoot ?? Path.GetTempPath(),
            environment);
    }

    private void RecordFailure<TDecision>(
        ResolvedPluginHook hook,
        string eventName,
        HookExecution<TDecision> execution)
        where TDecision : class
    {
        var detail = execution.FailureDetail is null
            ? execution.FailureKind
            : $"{execution.FailureKind}: {execution.FailureDetail}";
        RecordExecution(hook, eventName, FailureOutcome(execution.FailureKind!), detail, execution.Process);
    }

    private void RecordExecution(
        ResolvedPluginHook hook,
        string eventName,
        string outcome,
        string? detail,
        HookProcessResult? result)
    {
        _log.Append(new PluginHookExecutionEntry(
            DateTimeOffset.UtcNow,
            hook.PluginId,
            hook.Contribution.Id,
            eventName,
            _context.TurnId,
            outcome,
            result?.Duration.TotalMilliseconds ?? 0,
            result?.ExitCode,
            detail,
            result?.StderrTail));
        if (outcome is "failed" or "timedOut")
        {
            _logger.LogWarning(
                "Hook '{PluginId}/{HookId}' ({EventName}) ended as {Outcome}: {Detail}",
                hook.PluginId, hook.Contribution.Id, eventName, outcome, detail);
        }
    }

    private static string FailureOutcome(string failureKind)
        => failureKind == "timedOut" ? "timedOut" : "failed";

    private static string EventName(PluginHookEvent hookEvent)
        => hookEvent switch
        {
            PluginHookEvent.RunStarting => "runStarting",
            PluginHookEvent.RunCompleted => "runCompleted",
            PluginHookEvent.ToolExecuting => "toolExecuting",
            PluginHookEvent.ToolExecuted => "toolExecuted",
            PluginHookEvent.HttpRequestSending => "httpRequestSending",
            _ => "httpResponseReceived"
        };

    private static HookSource Source(ResolvedPluginHook hook) => new(hook.PluginId, hook.Contribution.Id);

    private RunStartingPayload BuildRunStartingPayload(ResolvedPluginHook hook, RunStartingInput input)
    {
        var (prompt, promptTruncated) = input.UserPrompt is null
            ? (null, false)
            : HookProtocol.Truncate(input.UserPrompt, HookProtocol.MaximumJsonValueBytes);
        return new RunStartingPayload(
            SchemaVersion,
            "runStarting",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            input.ProviderName,
            input.ProviderKind,
            input.Model,
            prompt,
            promptTruncated ? true : null,
            input.Attachments,
            input.Tools,
            input.CompletedSubagentTaskIds);
    }

    private RunCompletedPayload BuildRunCompletedPayload(ResolvedPluginHook hook, RunCompletedInput input)
    {
        var (finalText, truncated) = input.FinalText is null
            ? (null, false)
            : HookProtocol.Truncate(input.FinalText, HookProtocol.MaximumJsonValueBytes);
        return new RunCompletedPayload(
            SchemaVersion,
            "runCompleted",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            input.Status,
            finalText,
            truncated ? true : null,
            input.ErrorMessage,
            input.InputTokens is null && input.OutputTokens is null
                ? null
                : new HookUsagePayload(input.InputTokens, input.OutputTokens),
            input.ToolCallCount,
            input.Duration.TotalMilliseconds);
    }

    private ToolExecutingPayload BuildToolExecutingPayload(
        ResolvedPluginHook hook,
        ToolHookCall call,
        JsonElement arguments,
        bool argumentsTruncated)
        => new(
            SchemaVersion,
            "toolExecuting",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            call.CallId,
            call.Iteration,
            call.Binding.Descriptor.ProviderName,
            call.Binding.Descriptor.DisplayName,
            call.Binding.Descriptor.Kind,
            call.Binding.Descriptor.SourceKind,
            call.Binding.Descriptor.SourceId,
            arguments,
            argumentsTruncated ? true : null,
            call.Binding.RequiresApproval && call.PermissionMode != ToolPermissionMode.FullAccess,
            call.PermissionMode);

    private ToolExecutedPayload BuildToolExecutedPayload(
        ResolvedPluginHook hook,
        ToolHookCall call,
        ToolHookResult result,
        JsonElement arguments,
        bool argumentsTruncated,
        JsonElement? effectiveArguments)
    {
        var content = result.Content ?? JsonSerializer.SerializeToElement<string?>(null, HookProtocol.Options);
        var (contentValue, contentTruncated) = HookProtocol.TruncateJson(content);
        return new ToolExecutedPayload(
            SchemaVersion,
            "toolExecuted",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            call.CallId,
            call.Iteration,
            call.Binding.Descriptor.ProviderName,
            call.Binding.Descriptor.DisplayName,
            call.Binding.Descriptor.Kind,
            call.Binding.Descriptor.SourceKind,
            call.Binding.Descriptor.SourceId,
            arguments,
            argumentsTruncated ? true : null,
            call.Binding.RequiresApproval && call.PermissionMode != ToolPermissionMode.FullAccess,
            call.PermissionMode,
            effectiveArguments,
            ToolStatusName(result.Status),
            result.DeniedBy,
            result.Summary,
            contentValue,
            contentTruncated ? true : null,
            result.Error,
            result.Duration.TotalMilliseconds);
    }

    private HttpRequestSendingPayload BuildHttpRequestSendingPayload(
        ResolvedPluginHook hook,
        HttpHookRequest request)
    {
        var uri = request.Request.RequestUri;
        var url = uri is null ? string.Empty : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        return new HttpRequestSendingPayload(
            SchemaVersion,
            "httpRequestSending",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            request.Sequence,
            request.Request.Method.Method,
            url,
            QueryParameterNames(uri),
            HttpHeaderRedactor.Redact(request.Request.Headers, request.Request.Content?.Headers),
            hook.Contribution.IncludeRequestBody ? request.Body : null,
            hook.Contribution.IncludeRequestBody && request.BodyTruncated ? true : null);
    }

    private HttpResponseReceivedPayload BuildHttpResponseReceivedPayload(
        ResolvedPluginHook hook,
        HttpHookResponse response)
        => new(
            SchemaVersion,
            "httpResponseReceived",
            hook.PluginId,
            hook.Contribution.Id,
            _context.TurnId,
            _context.ConversationId,
            _context.Origin,
            hook.Inherited,
            _context.AgentId,
            _context.AgentName,
            _context.WorkspaceRoot,
            DateTimeOffset.UtcNow,
            response.Sequence,
            response.Response is null ? null : (int)response.Response.StatusCode,
            response.Response?.ReasonPhrase,
            response.Response is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : HttpHeaderRedactor.Redact(response.Response.Headers, response.Response.Content?.Headers),
            response.Elapsed.TotalMilliseconds,
            response.Error);

    private static IReadOnlyList<string> QueryParameterNames(Uri? uri)
    {
        if (string.IsNullOrEmpty(uri?.Query))
        {
            return [];
        }

        var query = uri.Query;
        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        var names = new List<string>();
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            names.Add(separator >= 0 ? segment[..separator] : segment);
        }

        return names;
    }

    private static (JsonElement Value, bool Truncated) TruncateArguments(AIFunctionArguments arguments)
        => HookProtocol.TruncateJson(SerializeArgumentsJson(arguments));

    private static JsonElement SerializeArgumentsJson(AIFunctionArguments arguments)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            map[pair.Key] = pair.Value;
        }

        return JsonSerializer.SerializeToElement(map, HookProtocol.Options);
    }

    private static AIFunctionArguments CreateArguments(JsonElement updated, AIFunctionArguments original)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in updated.EnumerateObject())
        {
            map[property.Name] = property.Value;
        }

        return new AIFunctionArguments(map)
        {
            Services = original.Services,
            Context = original.Context
        };
    }

    private static JsonElement? ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(value, HookProtocol.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToolStatusName(ToolCallStatus status)
        => status switch
        {
            ToolCallStatus.Completed => "completed",
            ToolCallStatus.Canceled => "canceled",
            ToolCallStatus.Blocked => "blocked",
            _ => "failed"
        };

    private sealed record HookExecution<TDecision>(
        HookDecisionParse<TDecision>? Parse,
        string? FailureKind,
        string? FailureDetail,
        HookProcessResult? Process)
        where TDecision : class;
}
