using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// In-process Direct runtime. It translates Microsoft.Extensions.AI streaming
/// content into the same provider-neutral event stream consumed by the desktop transcript.
/// </summary>
internal sealed class DirectAgentChatRuntime : IAgentRuntimeAdapter
{
    /// <summary>
    /// Reported when the model stops at its output-token cap. Hitting the cap is normal
    /// once the limit is configured, so this is informational rather than an error.
    /// </summary>
    private const string TruncatedMessage =
        "The response reached the configured output-token limit. Continue the message to " +
        "have the model resume from where it stopped.";

    /// <summary>
    /// Reported when the function-invoking tool loop stops while the model is still
    /// requesting tool calls, which means it hit <c>MaximumIterationsPerRequest</c>
    /// rather than finishing its work.
    /// </summary>
    private const string ToolLoopExhaustedMessage =
        "The response stopped while the model was still calling tools, which means the " +
        "tool-call loop hit its per-request iteration limit before the task finished.";

    /// <summary>
    /// Reported when the output-token cap is hit before any text is produced. There is no
    /// partial answer to resume from, so the limit is likely configured too low to be usable.
    /// </summary>
    private const string TruncatedWithoutOutputMessage =
        "The response reached the configured output-token limit without producing any output. " +
        "Raise the output-token limit for this model and try again.";

    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IDirectTurnCapabilityResolver _capabilityResolver;
    private readonly DirectPromptComposer _promptComposer;
    private readonly DirectTurnHooksFactory _hooksFactory;
    private readonly ILogger<DirectAgentChatRuntime> _logger;

    public DirectAgentChatRuntime(
        IAiChatClientFactory chatClientFactory,
        IDirectTurnCapabilityResolver capabilityResolver,
        DirectPromptComposer promptComposer,
        DirectTurnHooksFactory hooksFactory,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _capabilityResolver = capabilityResolver;
        _promptComposer = promptComposer;
        _hooksFactory = hooksFactory;
        _logger = logger ?? NullLogger<DirectAgentChatRuntime>.Instance;
    }

    public AgentExecutionMode Mode => AgentExecutionMode.Direct;

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The dispatcher routes by Mode, so a Direct turn always arrives as a DirectChatTurnRequest.
        var directRequest = request as DirectChatTurnRequest
            ?? throw new ArgumentException(
                $"The Direct runtime requires a {nameof(DirectChatTurnRequest)}.", nameof(request));
        return StreamCoreAsync(directRequest, cancellationToken);
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        DirectChatTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        // The linked source lets an abandoned enumerator (consumer stops reading without
        // cancelling) still tear down the provider stream instead of orphaning it.
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceEventsAsync(request, channel.Writer, producerCancellation.Token);

        try
        {
            await foreach (var streamEvent in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                yield return streamEvent;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task ProduceEventsAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var state = new TurnState();
        var output = new TurnOutputStream(writer);
        var cancellationObserved = false;
        var runCompletedEmitted = false;
        RunCompletedEvent? terminal = null;
        DirectTurnSetup? setup = null;
        try
        {
            state.Elapsed.Start();
            setup = await SetupTurnAsync(request, writer, state, cancellationToken).ConfigureAwait(false);
            if (setup.ProviderLease is null)
            {
                // SetupTurnAsync already wrote the Blocked terminal event.
                terminal = new RunCompletedEvent(RunCompletionStatus.Blocked, FinalText: null, setup.BlockReason);
                runCompletedEmitted = true;
            }
            else
            {
                var finishReason = await StreamResponseAsync(setup, output, cancellationToken).ConfigureAwait(false);
                output.ReportUsage();
                terminal = WriteTerminalOutcome(writer, finishReason, output);
                runCompletedEmitted = true;
            }
        }
        catch (OperationCanceledException exception)
        {
            cancellationObserved = true;
            writer.TryComplete(exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Direct AI agent turn failed.");
            output.ReportUsage();
            terminal = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                exception.Message);
            writer.TryWrite(terminal);
            runCompletedEmitted = true;
        }
        finally
        {
            state.Elapsed.Stop();
            DeliverRunCompleted(state, output, terminal, cancellationObserved);
            if (setup is not null)
            {
                await DisposeResourcesAsync(setup.ProviderLease, setup.CapabilityLease).ConfigureAwait(false);
            }

            if (!runCompletedEmitted && !cancellationObserved)
            {
                writer.TryWrite(Failed("The Direct AI agent turn ended without a completion status."));
            }

            writer.TryComplete();
        }
    }

    private void DeliverRunCompleted(
        TurnState state,
        TurnOutputStream output,
        RunCompletedEvent? terminal,
        bool cancellationObserved)
    {
        if (state.Hooks is null)
        {
            return;
        }

        try
        {
            var status = cancellationObserved
                ? "cancelled"
                : HookNotes.RunStatus(terminal?.Status ?? RunCompletionStatus.Failed);
            state.Hooks.RunCompleted(new RunCompletedInput(
                status,
                terminal?.FinalText,
                terminal?.ErrorMessage,
                output.InputTokensOrNull,
                output.OutputTokensOrNull,
                state.Invoker?.CallCount ?? 0,
                state.Elapsed.Elapsed));
        }
        catch (Exception exception)
        {
            // Delivery must never change the turn's terminal event.
            _logger.LogWarning(exception, "Failed to deliver the runCompleted hook event.");
        }
    }

    private async Task<DirectTurnSetup> SetupTurnAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        TurnState state,
        CancellationToken cancellationToken)
    {
        if (request.ExecutionContext.Origin == DirectTurnOrigin.Continuation && request.ToolExecutionCheckpoint is null)
        {
            throw new InvalidDataException("A continuation requires a durable tool execution checkpoint.");
        }

        var preparation = await _chatClientFactory.PrepareAsync(request.ModelProfileId, cancellationToken).ConfigureAwait(false);
        request = request with { ModelProfileId = preparation.Profile.Id };
        var capabilityLease = await _capabilityResolver.ResolveAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AiChatClientLease? providerLease = null;
        try
        {
            foreach (var diagnostic in capabilityLease.Diagnostics)
            {
                writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
            }

            foreach (var notice in capabilityLease.HookNotices)
            {
                writer.TryWrite(new RunNoticeEvent(notice));
            }

            // A hook Plugin inherited from the parent turn is missing or changed: the turn is
            // blocked rather than silently executed without its policy.
            if (capabilityLease.HookBlockReason is not null)
            {
                writer.TryWrite(new RunCompletedEvent(
                    RunCompletionStatus.Blocked, FinalText: null, ErrorMessage: capabilityLease.HookBlockReason));
                return new DirectTurnSetup(capabilityLease, null, null, [], capabilityLease.HookBlockReason);
            }

            var hooks = _hooksFactory.Create(
                new DirectHookTurnContext(
                    request.TurnId,
                    request.ConversationId,
                    request.ExecutionContext.Origin,
                    request.Agent.Id,
                    request.Agent.Name,
                    request.WorkspaceRoot?.RootPath,
                    preparation.Connection.Name,
                    preparation.Connection.ProviderKind,
                    preparation.Profile.Model),
                capabilityLease.Hooks);
            state.Hooks = hooks;
            if (hooks.HasRunStartingHooks)
            {
                writer.TryWrite(new RunStatusEvent(
                    AgentRunStatus.Initializing, "Running runStarting hooks…"));
            }

            var start = await hooks
                .RunStartingAsync(BuildRunStartingInput(request, preparation, capabilityLease), cancellationToken)
                .ConfigureAwait(false);
            foreach (var notice in start.Notices)
            {
                writer.TryWrite(new RunNoticeEvent(notice));
            }

            if (start.BlockedBy is not null)
            {
                writer.TryWrite(new RunCompletedEvent(
                    RunCompletionStatus.Blocked, FinalText: null, ErrorMessage: start.BlockReason));
                return new DirectTurnSetup(capabilityLease, null, null, [], start.BlockReason);
            }

            var invoker = new DirectToolInvoker(request, capabilityLease.Bindings, hooks);
            state.Invoker = invoker;
            var httpHandler = hooks.HasHttpHooks
                ? new HttpHookHandler(
                    hooks,
                    AiProviderHttpClientProvider.ReadExtraHeaderNames(preparation.Connection),
                    cancellationToken)
                : null;
            providerLease = _chatClientFactory.Create(
                preparation,
                new AiChatClientPipelineOptions(
                    capabilityLease.Tools,
                    invoker.InvokeAsync,
                    httpHandler));

            writer.TryWrite(new RunStartedEvent(
                $"direct-{Guid.NewGuid():N}",
                providerLease.Profile.Model,
                AgentKind: null));
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

            var messages = _promptComposer.BuildMessages(
                request.Messages,
                request.ToolExecutions ?? [],
                request.Agent.Instructions,
                capabilityLease.SystemInstructions,
                capabilityLease.MessageAdjustments,
                request.ExecutionContext,
                new DirectPromptBudget(
                    AiChatOptions.ResolveContextWindowTokens(providerLease.Profile),
                    providerLease.Options.MaxOutputTokens),
                providerLease.Options.Tools,
                start.Context);
            return new DirectTurnSetup(capabilityLease, providerLease, invoker, messages, null);
        }
        catch
        {
            // Ownership transfers to the producer only after the whole setup succeeds.
            await DisposeResourcesAsync(providerLease, capabilityLease).ConfigureAwait(false);
            throw;
        }
    }

    private static RunStartingInput BuildRunStartingInput(
        DirectChatTurnRequest request,
        AiProviderClientRequest preparation,
        DirectTurnCapabilityLease capabilityLease)
    {
        var latestUser = request.ExecutionContext.Origin == DirectTurnOrigin.Continuation
            ? null
            : request.Messages.LastOrDefault(message => message.Role == MessageRole.User);
        var attachments = latestUser?.Attachments is { Count: > 0 } records
            ? records.Select(record => new HookAttachmentPayload(
                record.FileName, record.MediaType, record.ByteLength)).ToArray()
            : [];
        return new RunStartingInput(
            preparation.Connection.Name,
            preparation.Connection.ProviderKind,
            preparation.Profile.Model,
            latestUser?.MarkdownContent,
            attachments,
            capabilityLease.Tools.Select(tool => tool.Name).ToArray(),
            request.ExecutionContext.CompletionBatch?.Deliveries.Select(delivery => delivery.TaskId.ToString("D")).ToArray());
    }

    /// <summary>
    /// Streams the provider response, translating each update into transcript events, and returns the
    /// finish reason of the final update.
    /// </summary>
    private async Task<ChatFinishReason?> StreamResponseAsync(
        DirectTurnSetup setup,
        TurnOutputStream output,
        CancellationToken cancellationToken)
    {
        // The M.E.AI FunctionInvokingChatClient owns the tool loop but never reports
        // that the model truncated its answer at the output-token cap
        // (FinishReason.Length). Left undetected that surfaces as output which "stops
        // for no reason" while the turn claims success. We detect the length stop and
        // report it as Truncated so the partial answer is kept and the decision to
        // continue - which costs another full request - stays with the user.
        ChatFinishReason? finishReason = null;
        await foreach (var update in setup.ProviderLease!.Client.GetStreamingResponseAsync(
                           setup.Messages,
                           setup.ProviderLease.Options,
                           cancellationToken).ConfigureAwait(false))
        {
            if (update.FinishReason is ChatFinishReason reason)
            {
                finishReason = reason;
            }

            output.TranslateUpdate(update, setup.CapabilityLease.Bindings, setup.Invoker);
        }

        return finishReason;
    }

    /// <summary>
    /// Emits the terminal RunCompleted event for the observed finish reason. A length stop with
    /// partial text is reported as Truncated (the partial answer is valid and kept in the prompt
    /// history, so the model can resume from it if the user continues); a length stop without text
    /// and an exhausted tool loop are both reported as Failed.
    /// </summary>
    private RunCompletedEvent WriteTerminalOutcome(
        ChannelWriter<AgentStreamEvent> writer,
        ChatFinishReason? finishReason,
        TurnOutputStream output)
    {
        if (finishReason == ChatFinishReason.Length && output.HasFinalText)
        {
            _logger.LogInformation(
                "Direct AI agent turn stopped at the output-token cap; reporting it as truncated.");
            var truncated = new RunCompletedEvent(
                RunCompletionStatus.Truncated,
                output.FinalText,
                TruncatedMessage);
            writer.TryWrite(truncated);
            return truncated;
        }

        if (finishReason == ChatFinishReason.Length)
        {
            _logger.LogWarning(
                "Direct AI agent turn hit the output-token cap without producing any text.");
            var failed = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                ErrorMessage: TruncatedWithoutOutputMessage,
                FinalText: null);
            writer.TryWrite(failed);
            return failed;
        }

        if (finishReason == ChatFinishReason.ToolCalls)
        {
            // FunctionInvokingChatClient resolves tool calls internally and only leaves
            // this finish reason on the final update when it stopped early - it hit
            // MaximumIterationsPerRequest while the model still wanted to call tools.
            // Surfacing it keeps the turn from looking like a clean finish.
            _logger.LogWarning(
                "Direct AI agent turn ended while the model was still requesting tool calls; " +
                "the tool-call loop hit its iteration limit.");
            var failed = new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                ToolLoopExhaustedMessage);
            writer.TryWrite(failed);
            return failed;
        }

        var succeeded = new RunCompletedEvent(
            RunCompletionStatus.Succeeded,
            output.FinalText,
            ErrorMessage: null);
        writer.TryWrite(succeeded);
        return succeeded;
    }

    private async Task DisposeResourcesAsync(
        AiChatClientLease? providerLease,
        DirectTurnCapabilityLease capabilityLease)
    {
        try
        {
            providerLease?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
        }

        try
        {
            await capabilityLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the Direct turn capability lease.");
        }
    }

    private static (ToolCallStatus Status, string? Summary, string? Detail) DescribeToolResult(
        FunctionResultContent content)
    {
        if (content.Exception is not null)
        {
            return (ToolCallStatus.Failed, content.Exception.Message, content.Exception.ToString());
        }

        return content.Result switch
        {
            DirectToolResult result => (result.Status, result.Summary, result.Detail),
            _ => (ToolCallStatus.Failed, "The tool returned an invalid Direct result.", null)
        };
    }

    private static int ClampTokens(long tokens) => (int)Math.Clamp(tokens, 0, int.MaxValue);

    private static RunCompletedEvent Failed(string message)
        => new(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);

    /// <summary>
    /// Per-turn mutable state that outlives <c>SetupTurnAsync</c>, so a turn that fails after its
    /// hooks were created still delivers exactly one <c>runCompleted</c>.
    /// </summary>
    private sealed class TurnState
    {
        public DirectTurnHooks? Hooks { get; set; }

        public DirectToolInvoker? Invoker { get; set; }

        public Stopwatch Elapsed { get; } = new();
    }

    /// <summary>
    /// One turn's accumulated stream translation: the final text, usage totals, and the descriptors
    /// of tool calls seen so far, reduced into transcript events on the shared channel.
    /// </summary>
    private sealed class TurnOutputStream(ChannelWriter<AgentStreamEvent> writer)
    {
        private readonly ChannelWriter<AgentStreamEvent> _writer = writer;
        private readonly StringBuilder _finalText = new();
        private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
        private long _inputTokens;
        private long _outputTokens;
        private bool _hasInputUsage;
        private bool _hasOutputUsage;
        private bool _usageWritten;

        public string FinalText => _finalText.ToString();

        public string? FinalTextOrNull => _finalText.Length == 0 ? null : _finalText.ToString();

        public bool HasFinalText => _finalText.Length > 0;

        public int? InputTokensOrNull => _hasInputUsage ? ClampTokens(_inputTokens) : null;

        public int? OutputTokensOrNull => _hasOutputUsage ? ClampTokens(_outputTokens) : null;

        public void TranslateUpdate(
            ChatResponseUpdate update,
            IReadOnlyDictionary<string, DirectToolBinding> bindings,
            DirectToolInvoker? invoker)
        {
            var blockId = string.IsNullOrWhiteSpace(update.MessageId)
                ? "direct-response"
                : update.MessageId;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        _finalText.Append(text.Text);
                        _writer.TryWrite(new AssistantTextDeltaEvent(blockId, text.Text));
                        break;

                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        _writer.TryWrite(new AssistantThinkingDeltaEvent(blockId, reasoning.Text));
                        break;

                    case FunctionCallContent call when _startedCalls.Add(call.CallId):
                        bindings.TryGetValue(call.Name, out var binding);
                        var descriptor = binding?.Descriptor;
                        var toolKind = descriptor?.Kind ?? ToolCallKind.Other;
                        _writer.TryWrite(new ToolCallStartedEvent(
                            call.CallId,
                            call.Name,
                            JsonSerializer.Serialize(call.Arguments),
                            toolKind,
                            descriptor?.SourceKind ?? ToolSourceKind.BuiltIn,
                            descriptor?.SourceId,
                            descriptor?.DisplayName));
                        break;

                    case FunctionResultContent result:
                        var (status, summary, detail) = DescribeToolResult(result);
                        _writer.TryWrite(new ToolCallCompletedEvent(
                            result.CallId,
                            status,
                            summary,
                            detail,
                            invoker?.TryTakeOutcome(result.CallId)));
                        break;

                    case UsageContent usage:
                        if (usage.Details.InputTokenCount is long input)
                        {
                            _hasInputUsage = true;
                            _inputTokens += input;
                        }

                        if (usage.Details.OutputTokenCount is long output)
                        {
                            _hasOutputUsage = true;
                            _outputTokens += output;
                        }

                        break;
                }
            }
        }

        /// <summary>Reports the turn's usage once; later calls are no-ops.</summary>
        public void ReportUsage()
        {
            if (_usageWritten)
            {
                return;
            }

            _usageWritten = true;
            if (_hasInputUsage || _hasOutputUsage)
            {
                _writer.TryWrite(new UsageReportedEvent(
                    _hasInputUsage ? ClampTokens(_inputTokens) : null,
                    _hasOutputUsage ? ClampTokens(_outputTokens) : null));
            }
        }
    }
}
