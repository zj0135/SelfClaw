using System.Runtime.CompilerServices;
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
            await foreach (var streamEvent in channel.Reader.ReadAllAsync())
            {
                yield return streamEvent;
            }
        }
        finally
        {
            producerCancellation.Cancel();
            await producer;
        }
    }

    private async Task ProduceEventsAsync(
        DirectChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        DirectTurnCapabilityLease? capabilityLease = null;
        AiChatClientLease? providerLease = null;
        var finalText = new System.Text.StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        var hasInputUsage = false;
        var hasOutputUsage = false;
        var usageReported = false;
        var startedCalls = new HashSet<string>(StringComparer.Ordinal);
        var startedDescriptors = new Dictionary<string, DirectToolDescriptor>(StringComparer.Ordinal);
        var runCompletedEmitted = false;
        var cancellationObserved = false;

        try
        {
            capabilityLease = await _capabilityResolver.ResolveAsync(request, cancellationToken)
                .ConfigureAwait(false);
            foreach (var diagnostic in capabilityLease.Diagnostics)
            {
                writer.TryWrite(new RunStatusEvent(AgentRunStatus.Initializing, diagnostic));
            }

            var inputs = new AiChatRuntimeInputs(EnableReasoning: false, capabilityLease.Tools);
            providerLease = request.ModelProfileId is Guid modelProfileId
                ? await _chatClientFactory.CreateAsync(modelProfileId, inputs, cancellationToken)
                : await _chatClientFactory.CreateForScopeAsync(
                    AiModelSelectionScopes.DesktopDefault,
                    inputs,
                    cancellationToken);

            writer.TryWrite(new RunStartedEvent(
                $"direct-{Guid.NewGuid():N}",
                providerLease.Profile.Model,
                AgentKind: null));
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

            var messages = _promptComposer.BuildMessages(
                request.Messages,
                request.Agent.Instructions,
                capabilityLease.SystemInstructions,
                capabilityLease.MessageAdjustments);
            await foreach (var update in providerLease.Client.GetStreamingResponseAsync(
                               messages,
                               providerLease.Options,
                               cancellationToken))
            {
                var blockId = string.IsNullOrWhiteSpace(update.MessageId)
                    ? "direct-response"
                    : update.MessageId;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            finalText.Append(text.Text);
                            writer.TryWrite(new AssistantTextDeltaEvent(blockId, text.Text));
                            break;

                        case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                            writer.TryWrite(new AssistantThinkingDeltaEvent(blockId, reasoning.Text));
                            break;

                        case FunctionCallContent call when startedCalls.Add(call.CallId):
                            capabilityLease.ToolDescriptors.TryGetValue(call.Name, out var descriptor);
                            var toolKind = descriptor?.Kind ?? ToolCallKind.Other;
                            if (descriptor is not null)
                            {
                                startedDescriptors[call.CallId] = descriptor;
                            }

                            writer.TryWrite(new ToolCallStartedEvent(
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
                                startedDescriptors.TryGetValue(result.CallId, out var resultDescriptor) &&
                                resultDescriptor.SourceKind == ToolSourceKind.Mcp
                                    ? McpToolAdapter.DescribeResult(result.Result)
                                    : DescribeToolResult(result);
                            writer.TryWrite(new ToolCallCompletedEvent(result.CallId, status, summary, detail));
                            break;

                        case UsageContent usage:
                            if (usage.Details.InputTokenCount is long input)
                            {
                                hasInputUsage = true;
                                inputTokens += input;
                            }

                            if (usage.Details.OutputTokenCount is long output)
                            {
                                hasOutputUsage = true;
                                outputTokens += output;
                            }

                            break;
                    }
                }
            }

            if (hasInputUsage || hasOutputUsage)
            {
                usageReported = TryWriteUsage(
                    writer,
                    hasInputUsage,
                    inputTokens,
                    hasOutputUsage,
                    outputTokens);
            }

            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Succeeded,
                finalText.ToString(),
                ErrorMessage: null));
            runCompletedEmitted = true;
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
            if (!usageReported)
            {
                TryWriteUsage(writer, hasInputUsage, inputTokens, hasOutputUsage, outputTokens);
            }

            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Failed,
                NullIfEmpty(finalText),
                exception.Message));
            runCompletedEmitted = true;
        }
        finally
        {
            try
            {
                providerLease?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
            }

            if (capabilityLease is not null)
            {
                try
                {
                    await capabilityLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to dispose the Direct turn capability lease.");
                }
            }

            if (!runCompletedEmitted && !cancellationObserved)
            {
                writer.TryWrite(Failed("The Direct AI agent turn ended without a completion status."));
            }

            writer.TryComplete();
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

    private static bool TryWriteUsage(
        ChannelWriter<AgentStreamEvent> writer,
        bool hasInputUsage,
        long inputTokens,
        bool hasOutputUsage,
        long outputTokens)
        => (hasInputUsage || hasOutputUsage) && writer.TryWrite(new UsageReportedEvent(
            hasInputUsage ? ClampTokens(inputTokens) : null,
            hasOutputUsage ? ClampTokens(outputTokens) : null));

    private static string? NullIfEmpty(System.Text.StringBuilder text)
        => text.Length == 0 ? null : text.ToString();

    private static RunCompletedEvent Failed(string message)
        => new(RunCompletionStatus.Failed, FinalText: null, ErrorMessage: message);
}
