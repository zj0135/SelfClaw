using SelfClaw.Desktop.Services.Notifications;
using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationCompletionNotifier : IConversationCompletionNotifier
{
    private readonly DesktopNotificationService _notificationService;

    public ConversationCompletionNotifier(DesktopNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public void Notify(ConversationRecord conversation, ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(messages);
        if (turn.ConversationId != conversation.Id)
        {
            throw new ArgumentException("The completed turn must belong to the conversation.", nameof(turn));
        }

        if (conversation.Mode != ConversationMode.Programming || turn.Status == ConversationTurnStatus.Running)
        {
            return;
        }

        _notificationService.ShowConversationCompleted(
            conversation.Id,
            ResolveTitle(conversation.Title, turn, messages),
            BuildMessage(turn, messages));
    }

    internal static string BuildMessage(ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(messages);
        var headline = turn.Status switch
        {
            ConversationTurnStatus.Blocked => "回合已被阻止",
            ConversationTurnStatus.Failed => "会话失败",
            ConversationTurnStatus.Cancelled => "会话已取消",
            ConversationTurnStatus.Truncated => "回答已截断",
            ConversationTurnStatus.Interrupted => "会话已中断",
            ConversationTurnStatus.Succeeded => "Programming session completed.",
            _ => throw new ArgumentException("Completion notification requires a terminal turn.", nameof(turn))
        };
        if (turn.Status != ConversationTurnStatus.Succeeded)
        {
            return WithReason(headline, turn.ErrorMessage);
        }

        var preview = messages
            .Where(message => message.TurnId == turn.Id && message.ConversationId == turn.ConversationId &&
                              message.Role == MessageRole.Assistant && message.Status == MessageStatus.Sealed)
            .OrderByDescending(message => message.Sequence)
            .Select(message => NormalizeText(message.MarkdownContent))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        return string.IsNullOrWhiteSpace(preview) ? headline : $"{headline}\n{Limit(preview, 140)}";
    }

    private static string WithReason(string headline, string? reason)
        => string.IsNullOrWhiteSpace(reason) ? $"{headline}。" : $"{headline}：{reason}";

    private static string ResolveTitle(string? fallbackTitle, ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages)
    {
        var latestPrompt = messages
            .Where(message => message.TurnId == turn.Id && message.ConversationId == turn.ConversationId &&
                              message.Status == MessageStatus.Sealed && message.Role == MessageRole.User)
            .OrderByDescending(message => message.Sequence)
            .Select(message => NormalizeText(message.MarkdownContent))
            .FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt));
        var resolved = latestPrompt ?? (string.IsNullOrWhiteSpace(fallbackTitle) ? "SelfClaw" : fallbackTitle.Trim());
        return Limit(resolved, 64);
    }

    private static string Limit(string text, int length) => text.Length > length ? text[..length] + "..." : text;

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var character in text)
        {
            var normalized = character switch
            {
                '\r' or '\n' or '\t' => ' ',
                '`' or '#' or '*' or '>' or '_' => ' ',
                _ => character
            };
            if (char.IsWhiteSpace(normalized))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                }

                previousWhitespace = true;
                continue;
            }

            builder.Append(normalized);
            previousWhitespace = false;
        }

        return builder.ToString().Trim();
    }
}
