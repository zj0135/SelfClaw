using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// One turn's stream translation: it reduces Microsoft.Extensions.AI updates into the shared
/// <see cref="AgentStreamEvent"/> stream - text and reasoning deltas, tool call/result pairs - so the runtime
/// holds the turn's control flow instead of provider payload handling. It also keeps the turn's final text,
/// which the runtime reports in the terminal event. Metering lives in <see cref="TurnUsageReporter"/>.
/// </summary>
internal sealed class DirectEventTranslator(ChannelWriter<AgentStreamEvent> writer)
{
    private readonly ChannelWriter<AgentStreamEvent> _writer = writer;
    private readonly StringBuilder _finalText = new();
    private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
    private readonly TurnUsageReporter _usage = new();

    public string FinalText => _finalText.ToString();

    public string? FinalTextOrNull => _finalText.Length == 0 ? null : _finalText.ToString();

    /// <summary>
    /// Whether the current segment produced text. An input boundary seals the assistant message and starts
    /// a new segment, so this can never answer for an earlier one.
    /// </summary>
    public bool HasFinalText => _finalText.Length > 0;

    public void PublishInputBoundary(ConversationInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Write(new TurnInputBoundaryEvent(batch, _usage.Build()));
    }

    /// <summary>
    /// Publishes a lifecycle phase. The in-turn compaction path reports through here so its activity text is
    /// covered by the same overwrite rule as the turn-start path.
    /// </summary>
    public void PublishRunStatus(AgentRunStatus status, string? detail = null)
        => Write(new RunStatusEvent(status, detail));

    public void PublishInTurnCompactionStarted(int estimatedTokens, int thresholdTokens)
        => Write(new ContextCompactionStartedEvent(estimatedTokens, thresholdTokens));

    public void PublishInTurnCompactionCompleted(InTurnContextCompactionCompletedEvent compaction)
    {
        ArgumentNullException.ThrowIfNull(compaction);
        Write(compaction);
    }

    public void PublishInTurnCompactionFailed(string reason)
        => Write(new ContextCompactionFailedEvent(reason));

    public void BeginInputSegment() => _finalText.Clear();

    /// <summary>
    /// Supplies the pricing, model id and context window the accumulated usage is reported with.
    /// The translator has no provider or model knowledge of its own.
    /// </summary>
    public void ConfigureUsage(AiModelConfiguration? configuration, string? model, int? contextWindowTokens)
        => _usage.Configure(configuration, model, contextWindowTokens);

    public void TranslateUpdate(
        ChatResponseUpdate update,
        IReadOnlyDictionary<string, DirectToolBinding> bindings)
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

                case ErrorContent { ErrorCode: "Refusal" } refusal when !string.IsNullOrEmpty(refusal.Message):
                    _finalText.Append(refusal.Message);
                    _writer.TryWrite(new AssistantTextDeltaEvent(blockId, refusal.Message));
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
                        descriptor is null ? null : DirectToolSource.ResolveId(descriptor, call.Arguments),
                        descriptor?.DisplayName));
                    break;

                case FunctionResultContent result:
                    var (status, summary, detail) = DescribeToolResult(result);
                    _writer.TryWrite(new ToolCallCompletedEvent(
                        result.CallId,
                        status,
                        summary,
                        detail));
                    break;

            }
        }
    }

    /// <summary>Reports the turn's usage once; later calls are no-ops.</summary>
    public void ReportUsage() => _usage.Report(_writer);

    /// <summary>Records one provider call; every observation counts as one request.</summary>
    public void ObserveUsage(UsageDetails details) => _usage.Observe(details);

    private void Write(AgentStreamEvent streamEvent)
    {
        if (!_writer.TryWrite(streamEvent))
            throw new InvalidOperationException("The event consumer closed before the event was published.");
    }

    /// <summary>
    /// Reads the transcript-facing status, summary and display detail out of a tool result. A provider-side
    /// exception only happens when the invoker rethrew (its own fault budget is spent, or the failure is not
    /// a tool fault at all), so it must not be reported as a completed call.
    /// </summary>
    private static (ToolCallStatus Status, string? Summary, string? Detail) DescribeToolResult(
        FunctionResultContent content)
    {
        if (content.Exception is not null)
        {
            // Replayed on later turns, so only the message is kept; the stack trace would be prompt noise.
            return (ToolCallStatus.Failed, content.Exception.Message, content.Exception.Message);
        }

        return content.Result switch
        {
            DirectToolResult result => (result.Status, result.Summary, result.Detail),
            _ => (ToolCallStatus.Failed, "The tool returned an invalid Direct result.", null)
        };
    }
}
