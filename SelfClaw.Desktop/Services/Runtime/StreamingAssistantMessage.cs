using System.Text;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class StreamingAssistantMessage
{
    private readonly StringBuilder _completedMarkdown;
    private StringBuilder? _activeThinking;

    public StreamingAssistantMessage(string initialMarkdown, DateTimeOffset updatedAtUtc)
    {
        _completedMarkdown = new StringBuilder(initialMarkdown);
        UpdatedAtUtc = updatedAtUtc;
    }

    public long Revision { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void AppendText(string markdown, DateTimeOffset updatedAtUtc)
    {
        CompleteThinkingCore();
        _completedMarkdown.Append(markdown);
        MarkChanged(updatedAtUtc);
    }

    public void AppendThinking(string markdown, DateTimeOffset updatedAtUtc)
    {
        _activeThinking ??= new StringBuilder();
        _activeThinking.Append(markdown);
        MarkChanged(updatedAtUtc);
    }

    public void AppendToolAnchor(Guid toolExecutionId, DateTimeOffset updatedAtUtc)
    {
        CompleteThinkingCore();
        _completedMarkdown.Append(AssistantMessageSegmenter.AppendToolAnchor(null, toolExecutionId));
        MarkChanged(updatedAtUtc);
    }

    public void CompleteThinking(DateTimeOffset updatedAtUtc)
    {
        if (CompleteThinkingCore())
        {
            MarkChanged(updatedAtUtc);
        }
    }

    public string Snapshot()
    {
        var completed = _completedMarkdown.ToString();
        return _activeThinking is null
            ? completed
            : string.Concat(completed, AssistantMessageSegmenter.WrapThinking(_activeThinking.ToString()));
    }

    private bool CompleteThinkingCore()
    {
        if (_activeThinking is null)
        {
            return false;
        }

        _completedMarkdown.Append(AssistantMessageSegmenter.WrapThinking(_activeThinking.ToString()));
        _activeThinking = null;
        return true;
    }

    private void MarkChanged(DateTimeOffset updatedAtUtc)
    {
        Revision = checked(Revision + 1);
        UpdatedAtUtc = updatedAtUtc;
    }
}
