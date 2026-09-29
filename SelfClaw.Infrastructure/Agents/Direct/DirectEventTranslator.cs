using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// One turn's stream translation: it reduces Microsoft.Extensions.AI updates into the shared
/// <see cref="AgentStreamEvent"/> stream - text and reasoning deltas, tool call/result pairs, and one
/// aggregated usage report - so the runtime holds the turn's control flow instead of provider payload
/// handling. It also keeps the turn's final text, which the runtime reports in the terminal event.
/// </summary>
internal sealed class DirectEventTranslator(ChannelWriter<AgentStreamEvent> writer)
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
            return (ToolCallStatus.Failed, content.Exception.Message, content.Exception.ToString());
        }

        return content.Result switch
        {
            DirectToolResult result => (result.Status, result.Summary, result.Detail),
            _ => (ToolCallStatus.Failed, "The tool returned an invalid Direct result.", null)
        };
    }

    private static int ClampTokens(long tokens) => (int)Math.Clamp(tokens, 0, int.MaxValue);
}
