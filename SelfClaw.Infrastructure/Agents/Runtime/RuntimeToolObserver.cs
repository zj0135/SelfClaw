using System.Diagnostics;
using System.Threading.Channels;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class RuntimeToolObserver
{
    private readonly ChannelWriter<ChatRuntimeEvent> _writer;
    private readonly Guid _conversationId;
    private readonly Guid? _agentId;
    private readonly Guid? _messageId;
    private readonly Dictionary<string, Stopwatch> _stopwatches = new(StringComparer.Ordinal);

    public RuntimeToolObserver(
        ChannelWriter<ChatRuntimeEvent> writer,
        Guid conversationId,
        Guid? agentId,
        Guid? messageId)
    {
        _writer = writer;
        _conversationId = conversationId;
        _agentId = agentId;
        _messageId = messageId;
    }

    public ToolExecutionRecord Start(string toolName, string argumentsJson)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var record = new ToolExecutionRecord(
            Guid.NewGuid(),
            _conversationId,
            toolName,
            argumentsJson,
            ToolExecutionStatus.Running,
            null,
            correlationId,
            null,
            now,
            now,
            _agentId,
            _messageId,
            null);

        _stopwatches[correlationId] = Stopwatch.StartNew();
        _writer.TryWrite(new ToolExecutionStartedEvent(record));
        return record;
    }

    public void Complete(ToolExecutionRecord record, string summary)
        => Finish(record, ToolExecutionStatus.Completed, summary);

    public void Complete(ToolExecutionRecord record, string summary, string? resultContent)
        => Finish(record, ToolExecutionStatus.Completed, summary, resultContent);

    public void Fail(ToolExecutionRecord record, string message)
        => Finish(record, ToolExecutionStatus.Failed, message);

    public void Fail(ToolExecutionRecord record, string message, string? resultContent)
        => Finish(record, ToolExecutionStatus.Failed, message, resultContent);

    public ToolExecutionRecord AwaitApproval(ToolExecutionRecord record, string summary)
        => Update(record, ToolExecutionStatus.AwaitingApproval, summary);

    public ToolExecutionRecord Resume(ToolExecutionRecord record, string summary)
        => Update(record, ToolExecutionStatus.Running, summary);

    public void Cancel(ToolExecutionRecord record, string message)
        => Finish(record, ToolExecutionStatus.Cancelled, message);

    private ToolExecutionRecord Update(ToolExecutionRecord record, ToolExecutionStatus status, string summary)
    {
        var updated = record with
        {
            Status = status,
            ResultSummary = summary,
            ResultContent = record.ResultContent,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        _writer.TryWrite(new ToolExecutionCompletedEvent(updated));
        return updated;
    }

    private void Finish(ToolExecutionRecord record, ToolExecutionStatus status, string summary, string? resultContent = null)
    {
        var now = DateTimeOffset.UtcNow;
        var durationMs = TryStop(record.CorrelationId);
        var updated = record with
        {
            Status = status,
            ResultSummary = summary,
            ResultContent = resultContent ?? record.ResultContent,
            DurationMs = durationMs,
            UpdatedAtUtc = now
        };

        _writer.TryWrite(new ToolExecutionCompletedEvent(updated));
    }

    private double? TryStop(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !_stopwatches.Remove(correlationId, out var stopwatch))
        {
            return null;
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
