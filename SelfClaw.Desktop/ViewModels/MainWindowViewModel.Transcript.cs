using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ApplyAssistantDelta(ConversationRuntimeState runtimeState, Guid messageId, string deltaMarkdown)
    {
        if (string.IsNullOrWhiteSpace(deltaMarkdown))
        {
            return;
        }

        var message = runtimeState.Messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return;
        }

        var updated = message with
        {
            MarkdownContent = message.MarkdownContent + deltaMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        ReplaceMessage(runtimeState, updated);
        PublishRuntimeState(runtimeState, true);
    }

    private async Task FailActiveMessagesAsync(
        ConversationRuntimeState runtimeState,
        IEnumerable<Guid> messageIds,
        string errorMessage)
    {
        foreach (var messageId in messageIds.ToArray())
        {
            var existing = runtimeState.Messages.FirstOrDefault(item => item.Id == messageId);
            if (existing is null)
            {
                continue;
            }

            var updated = existing with
            {
                Status = MessageStatus.Failed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            };

            ReplaceMessage(runtimeState, updated);
            await _conversationRepository.UpsertMessageAsync(updated);
        }
    }

    private ToolExecutionRecord CaptureToolRunAnchor(ConversationRuntimeState runtimeState, ToolExecutionRecord toolRun)
    {
        if (runtimeState.ToolRunAnchors.TryGetValue(toolRun.Id, out var existingAnchor))
        {
            return toolRun with
            {
                MessageId = existingAnchor.MessageId,
                AfterSegmentIndex = existingAnchor.AfterSegmentIndex
            };
        }

        if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
        {
            runtimeState.ToolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            return toolRun;
        }

        if (toolRun.MessageId is not Guid anchoredMessageId)
        {
            return toolRun;
        }

        var message = runtimeState.Messages.FirstOrDefault(item => item.Id == anchoredMessageId);
        if (message is null)
        {
            return toolRun;
        }

        var anchoredMarkdown = AssistantMessageSegmenter.AppendToolAnchor(message.MarkdownContent, toolRun.Id);
        var anchoredSegments = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(anchoredMarkdown).Segments
            : [];
        var anchorIndex = anchoredSegments
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry =>
                entry.item.Kind == AssistantMessageSegmentKind.ToolAnchor &&
                entry.item.ToolExecutionId == toolRun.Id)
            .index;
        var anchorAfterSegmentIndex = anchorIndex > 0 ? anchorIndex - 1 : -1;
        var anchor = new ToolRunAnchor(anchoredMessageId, anchorAfterSegmentIndex);

        ReplaceMessage(runtimeState, message with
        {
            MarkdownContent = anchoredMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        runtimeState.ToolRunAnchors[toolRun.Id] = anchor;
        return toolRun with
        {
            MessageId = anchor.MessageId,
            AfterSegmentIndex = anchor.AfterSegmentIndex
        };
    }

    private static string BuildAvatarText(MessageRecord message)
    {
        if (message.Role == MessageRole.User)
        {
            return "You";
        }

        if (!string.IsNullOrWhiteSpace(message.AgentName))
        {
            var tokens = message.AgentName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2)
            {
                return string.Concat(tokens[0][0], tokens[1][0]).ToUpperInvariant();
            }

            return message.AgentName.Length <= 2
                ? message.AgentName.ToUpperInvariant()
                : message.AgentName[..2].ToUpperInvariant();
        }

        return message.Role == MessageRole.Assistant ? "SC" : "SYS";
    }
}
