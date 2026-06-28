using System.Text;
using System.Windows;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string NotificationToolAnchorPrefix = "<!--selfclaw:tool:";

    /// <summary>
    /// 回合结束后弹出"对话完成"系统通知。由 <c>SendAsync</c> 在流式结束时调用，是发送→渲染主路径的一部分。
    /// </summary>
    private void PublishConversationCompletedNotification(
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord>? messages = null)
    {
        if (conversation.Mode != ConversationMode.Programming)
        {
            return;
        }

        messages ??= _messages;
        var title = ResolveNotificationTitle(conversation.Id, conversation.Title, messages);
        var message = BuildConversationCompletedMessage(messages);
        _desktopNotificationService.ShowConversationCompleted(
            conversation.Id,
            title,
            message);
    }

    private static string BuildConversationCompletedMessage(IReadOnlyList<MessageRecord> messages)
    {
        const string modeMessage = "Programming session completed.";
        var preview = BuildConversationPreview(messages);

        return string.IsNullOrWhiteSpace(preview)
            ? modeMessage
            : $"{modeMessage}\n{preview}";
    }

    private static string BuildConversationPreview(IReadOnlyList<MessageRecord> messages)
    {
        var latestMessage = messages
            .Where(message => message.Status == MessageStatus.Completed && message.Role == MessageRole.Assistant)
            .OrderByDescending(message => message.CreatedAtUtc)
            .FirstOrDefault()
            ?? messages
            .Where(message => message.Status == MessageStatus.Completed && message.Role is MessageRole.Assistant or MessageRole.System)
            .OrderByDescending(message => message.CreatedAtUtc)
            .FirstOrDefault();

        if (latestMessage is null)
        {
            return string.Empty;
        }

        var preview = NormalizeNotificationText(latestMessage.MarkdownContent);
        if (string.IsNullOrWhiteSpace(preview) && !string.IsNullOrWhiteSpace(latestMessage.ErrorMessage))
        {
            preview = NormalizeNotificationText(latestMessage.ErrorMessage);
        }

        return preview.Length > 140 ? preview[..140] + "..." : preview;
    }

    private string ResolveNotificationTitle(
        Guid? conversationId,
        string? fallbackTitle,
        IReadOnlyList<MessageRecord>? messages = null)
    {
        messages ??= _messages;
        var latestPrompt = messages
            .Where(message => message.Status == MessageStatus.Completed && message.Role == MessageRole.User)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Select(message => NormalizeNotificationText(message.MarkdownContent))
            .FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt));

        if (!string.IsNullOrWhiteSpace(latestPrompt))
        {
            return latestPrompt.Length > 64 ? latestPrompt[..64] + "..." : latestPrompt;
        }

        var conversationTitle = conversationId is Guid id
            ? _allConversations.FirstOrDefault(item => item.Id == id)?.Title
            : null;
        var resolved = string.IsNullOrWhiteSpace(conversationTitle)
            ? fallbackTitle
            : conversationTitle;

        resolved = string.IsNullOrWhiteSpace(resolved) ? "SelfClaw" : resolved.Trim();
        return resolved.Length > 64 ? resolved[..64] + "..." : resolved;
    }

    private static string NormalizeNotificationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sanitized = RemoveNotificationMetadata(text);
        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;

        foreach (var character in sanitized)
        {
            var normalized = character switch
            {
                '\r' or '\n' or '\t' => ' ',
                '`' or '#' or '*' or '>' or '_' => ' ',
                _ => character
            };

            if (char.IsWhiteSpace(normalized))
            {
                if (previousWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWhitespace = true;
                continue;
            }

            builder.Append(normalized);
            previousWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static string RemoveNotificationMetadata(string text)
    {
        var startIndex = text.IndexOf(NotificationToolAnchorPrefix, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var segmentStart = 0;

        while (startIndex >= 0)
        {
            if (startIndex > segmentStart)
            {
                builder.Append(text, segmentStart, startIndex - segmentStart);
            }

            var endIndex = text.IndexOf("-->", startIndex, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                return builder.ToString();
            }

            segmentStart = endIndex + 3;
            startIndex = text.IndexOf(NotificationToolAnchorPrefix, segmentStart, StringComparison.Ordinal);
        }

        if (segmentStart < text.Length)
        {
            builder.Append(text, segmentStart, text.Length - segmentStart);
        }

        return builder.ToString();
    }
}
