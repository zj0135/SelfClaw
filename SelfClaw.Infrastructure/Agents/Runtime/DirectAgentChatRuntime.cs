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
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Infrastructure.Agents.Runtime;

/// <summary>
/// In-process Direct runtime. It translates Microsoft.Extensions.AI streaming
/// content into the same provider-neutral event stream consumed by the desktop transcript.
/// </summary>
public sealed class DirectAgentChatRuntime : IAgentChatRuntime
{
    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly WorkspaceAgentToolset _workspaceToolset;
    private readonly ILogger<DirectAgentChatRuntime> _logger;

    public DirectAgentChatRuntime(
        IAiChatClientFactory chatClientFactory,
        WorkspaceAgentToolset workspaceToolset,
        ILogger<DirectAgentChatRuntime>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _workspaceToolset = workspaceToolset;
        _logger = logger ?? NullLogger<DirectAgentChatRuntime>.Instance;
    }

    public IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StreamCoreAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamCoreAsync(
        ChatTurnRequest request,
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
        ChatTurnRequest request,
        ChannelWriter<AgentStreamEvent> writer,
        CancellationToken cancellationToken)
    {
        AiChatClientLease? lease = null;
        var finalText = new System.Text.StringBuilder();
        long inputTokens = 0;
        long outputTokens = 0;
        var hasInputUsage = false;
        var hasOutputUsage = false;
        var usageReported = false;
        var startedCalls = new HashSet<string>(StringComparer.Ordinal);
        var runCompletedEmitted = false;

        try
        {
            var tools = request.WorkspaceRoot is null
                ? Array.Empty<AITool>()
                : _workspaceToolset.CreateTools(
                    request.WorkspaceRoot,
                    request.ConversationId,
                    request.ToolPermissionMode,
                    request.ToolApprovalHandler).ToArray();
            var inputs = new AiChatRuntimeInputs(EnableReasoning: false, tools);
            lease = request.ModelProfileId is Guid modelProfileId
                ? await _chatClientFactory.CreateAsync(modelProfileId, inputs, cancellationToken)
                : await _chatClientFactory.CreateForScopeAsync(
                    AiModelSelectionScopes.DesktopDefault,
                    inputs,
                    cancellationToken);

            writer.TryWrite(new RunStartedEvent(
                $"direct-{Guid.NewGuid():N}",
                lease.Profile.Model,
                AgentKind: null));
            writer.TryWrite(new RunStatusEvent(AgentRunStatus.Requesting));

            var messages = BuildMessages(request);
            await foreach (var update in lease.Client.GetStreamingResponseAsync(
                               messages,
                               lease.Options,
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
                            writer.TryWrite(new ToolCallStartedEvent(
                                call.CallId,
                                call.Name,
                                JsonSerializer.Serialize(call.Arguments),
                                MapToolKind(call.Name)));
                            break;

                        case FunctionResultContent result:
                            var (status, summary, detail) = DescribeToolResult(result);
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
        catch (OperationCanceledException)
        {
            if (!usageReported)
            {
                TryWriteUsage(writer, hasInputUsage, inputTokens, hasOutputUsage, outputTokens);
            }

            writer.TryWrite(new RunCompletedEvent(
                RunCompletionStatus.Canceled,
                NullIfEmpty(finalText),
                ErrorMessage: null));
            runCompletedEmitted = true;
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
                lease?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to dispose the Direct AI chat client pipeline.");
            }

            if (!runCompletedEmitted)
            {
                writer.TryWrite(Failed("The Direct AI agent turn ended without a completion status."));
            }

            writer.TryComplete();
        }
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(ChatTurnRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.Agent.Instructions))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.Agent.Instructions));
        }

        foreach (var message in request.Messages)
        {
            if (message.Status == MessageStatus.Failed || string.IsNullOrEmpty(message.MarkdownContent))
            {
                continue;
            }

            ChatRole? role = message.Role switch
            {
                MessageRole.User => ChatRole.User,
                MessageRole.Assistant => ChatRole.Assistant,
                _ => null
            };
            if (role is ChatRole chatRole)
            {
                messages.Add(new ChatMessage(chatRole, message.MarkdownContent));
            }
        }

        return messages;
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

    private static ToolCallKind MapToolKind(string name) => name.ToLowerInvariant() switch
    {
        "read_file" => ToolCallKind.Read,
        "write_file" => ToolCallKind.Edit,
        "run_shell_command" => ToolCallKind.Run,
        "search_text" => ToolCallKind.Search,
        "list_files" => ToolCallKind.List,
        _ => ToolCallKind.Other
    };

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
