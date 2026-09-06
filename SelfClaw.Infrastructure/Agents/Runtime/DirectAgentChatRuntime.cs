using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime.Abstractions;
using SelfClaw.Infrastructure.Agents.Runtime.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Infrastructure.Agents.Runtime;

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
    private readonly ILogger<DirectAgentChatRuntime> _logger;

    public DirectAgentChatRuntime(
        IAiChatClientFactory chatClientFactory,
        IDirectTurnCapabilityResolver capabilityResolver,
        DirectPromptComposer promptComposer,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _capabilityResolver = capabilityResolver;
        _promptComposer = promptComposer;
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
        var output = new TurnOutputStream(writer);
        var cancellationObserved = false;
        var runCompletedEmitted = false;
        DirectTurnSetup? setup = null;
        try
        {
            setup = await SetupTurnAsync(request, writer, cancellationToken).ConfigureAwait(false);
            var finishReason = await StreamResponseAsync(setup, output, cancellationToken).ConfigureAwait(false);
            output.ReportUsage();
            runCompletedEmitted = WriteTerminalOutcome(writer, finishReason, output);
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
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                exception.Message));
            runCompletedEmitted = true;
        }
        finally
        {
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

    private async Task<DirectTurnSetup> SetupTurnAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        var capabilityLease = await _capabilityResolver.ResolveAsync(request, cancellationToken)
            .ConfigureAwait(false);
        AiChatClientLease? providerLease = null;
        try
        {
            foreach (var diagnostic in capabilityLease.Diagnostics)
            {
                writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
            }

            var inputs = new AiChatRuntimeInputs(EnableReasoning: false, capabilityLease.Tools);
            providerLease = request.ModelProfileId is Guid modelProfileId
                ? await _chatClientFactory.CreateAsync(modelProfileId, inputs, cancellationToken).ConfigureAwait(false)
                : await _chatClientFactory.CreateForScopeAsync(
                    AiModelSelectionScopes.DesktopDefault,
                    inputs,
                    cancellationToken).ConfigureAwait(false);

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
                providerLease.Options.Tools);
            return new DirectTurnSetup(capabilityLease, providerLease, messages);
        }
        catch
        {
            // Ownership transfers to the producer only after the whole setup succeeds.
            await DisposeResourcesAsync(providerLease, capabilityLease).ConfigureAwait(false);
            throw;
        }
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
        await foreach (var update in setup.ProviderLease.Client.GetStreamingResponseAsync(
                           setup.Messages,
                           setup.ProviderLease.Options,
                           cancellationToken).ConfigureAwait(false))
        {
            if (update.FinishReason is ChatFinishReason reason)
            {
                finishReason = reason;
            }

            output.TranslateUpdate(update, setup.CapabilityLease.ToolDescriptors);
        }

        return finishReason;
    }

    /// <summary>
    /// Emits the terminal RunCompleted event for the observed finish reason. A length stop with
    /// partial text is reported as Truncated (the partial answer is valid and kept in the prompt
    /// history, so the model can resume from it if the user continues); a length stop without text
    /// and an exhausted tool loop are both reported as Failed.
    /// </summary>
    private bool WriteTerminalOutcome(
        ChannelWriter<AgentStreamEvent> writer,
        ChatFinishReason? finishReason,
        TurnOutputStream output)
    {
        if (finishReason == ChatFinishReason.Length && output.HasFinalText)
        {
            _logger.LogInformation(
                "Direct AI agent turn stopped at the output-token cap; reporting it as truncated.");
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Truncated,
                output.FinalText,
                TruncatedMessage));
            return true;
        }

        if (finishReason == ChatFinishReason.Length)
        {
            _logger.LogWarning(
                "Direct AI agent turn hit the output-token cap without producing any text.");
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                ErrorMessage: TruncatedWithoutOutputMessage,
                FinalText: null));
            return true;
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
            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                output.FinalTextOrNull,
                ToolLoopExhaustedMessage));
            return true;
        }

        writer.TryWrite(new RunCompletedEvent(
            RunCompletionStatus.Succeeded,
            output.FinalText,
            ErrorMessage: null));
        return true;
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
            IReadOnlyList<WorkspaceFileEntry> entries =>
                (ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(entries), WorkspaceToolSummaries.Describe(entries)),
            IReadOnlyList<WorkspaceSearchHit> hits =>
                (ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(hits), WorkspaceToolSummaries.Describe(hits)),
            WorkspaceFileContent file =>
                (ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(file), WorkspaceToolSummaries.Describe(file)),
            WorkspaceFileWriteResult write =>
                (ToolCallStatus.Completed, WorkspaceToolSummaries.Summarize(write), WorkspaceToolSummaries.Describe(write)),
            ShellCommandResult shell =>
                (shell.ExitCode is null or 0 ? ToolCallStatus.Completed : ToolCallStatus.Failed,
                    WorkspaceToolSummaries.Summarize(shell),
                    WorkspaceToolSummaries.Describe(shell)),
            JsonElement json => (ToolCallStatus.Completed, "Tool call completed.", JsonElementText(json)),
            null => (ToolCallStatus.Completed, "Tool call completed.", null),
            _ => (ToolCallStatus.Completed, "Tool call completed.", content.Result.ToString())
        };
    }

    private static string JsonElementText(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

    private static int ClampTokens(long tokens) => (int)Math.Clamp(tokens, 0, int.MaxValue);

    private static RunCompletedEvent Failed(string message)
        => new(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);

    /// <summary>
    /// One turn's accumulated stream translation: the final text, usage totals, and the descriptors
    /// of tool calls seen so far, reduced into transcript events on the shared channel.
    /// </summary>
    private sealed class TurnOutputStream(ChannelWriter<AgentStreamEvent> writer)
    {
        private readonly ChannelWriter<AgentStreamEvent> _writer = writer;
        private readonly StringBuilder _finalText = new();
        private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DirectToolDescriptor> _startedDescriptors = new(StringComparer.Ordinal);
        private long _inputTokens;
        private long _outputTokens;
        private bool _hasInputUsage;
        private bool _hasOutputUsage;
        private bool _usageWritten;

        public string FinalText => _finalText.ToString();

        public string? FinalTextOrNull => _finalText.Length == 0 ? null : _finalText.ToString();

        public bool HasFinalText => _finalText.Length > 0;

        public void TranslateUpdate(
            ChatResponseUpdate update,
            IReadOnlyDictionary<string, DirectToolDescriptor> toolDescriptors)
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
                        toolDescriptors.TryGetValue(call.Name, out var descriptor);
                        var toolKind = descriptor?.Kind ?? ToolCallKind.Other;
                        if (descriptor is not null)
                        {
                            _startedDescriptors[call.CallId] = descriptor;
                        }

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
                        var (status, summary, detail) =
                            result.Exception is null &&
                            _startedDescriptors.TryGetValue(result.CallId, out var resultDescriptor) &&
                            resultDescriptor.SourceKind == ToolSourceKind.Mcp
                                ? McpToolAdapter.DescribeResult(result.Result)
                                : DescribeToolResult(result);
                        _writer.TryWrite(new ToolCallCompletedEvent(result.CallId, status, summary, detail));
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
        public bool ReportUsage()
        {
            if (_usageWritten)
            {
                return false;
            }

            _usageWritten = true;
            return (_hasInputUsage || _hasOutputUsage) && _writer.TryWrite(new UsageReportedEvent(
                _hasInputUsage ? ClampTokens(_inputTokens) : null,
                _hasOutputUsage ? ClampTokens(_outputTokens) : null));
        }
    }
}
