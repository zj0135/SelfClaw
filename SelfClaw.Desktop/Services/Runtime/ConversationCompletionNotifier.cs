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

    public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(messages);

        if (conversation.Mode != ConversationMode.Programming)
        {
            return;
        }

        _notificationService.ShowConversationCompleted(
            conversation.Id,
            ResolveTitle(conversation.Title, messages),
            BuildMessage(messages));
    }

    private static string BuildMessage(IReadOnlyList<MessageRecord> messages)
    {
        const string modeMessage = "Programming session completed.";
        var preview = BuildPreview(messages);
        return string.IsNullOrWhiteSpace(preview)
            ? modeMessage
            : $"{modeMessage}\n{preview}";
    }

    private static string BuildPreview(IReadOnlyList<MessageRecord> messages)
    {
        var latestMessage = messages
            .Where(message => message.Status is MessageStatus.Completed or MessageStatus.Truncated
                              && message.Role == MessageRole.Assistant)
            .OrderByDescending(message => message.CreatedAtUtc)
            .FirstOrDefault()
            ?? messages
                .Where(message => message.Status is MessageStatus.Completed or MessageStatus.Truncated &&
                                  message.Role is MessageRole.Assistant or MessageRole.System)
                .OrderByDescending(message => message.CreatedAtUtc)
                .FirstOrDefault();
        if (latestMessage is null)
        {
            return string.Empty;
        }

        var preview = NormalizeText(latestMessage.MarkdownContent);
        if (string.IsNullOrWhiteSpace(preview) && !string.IsNullOrWhiteSpace(latestMessage.ErrorMessage))
        {
            preview = NormalizeText(latestMessage.ErrorMessage);
        }

        return preview.Length > 140 ? preview[..140] + "..." : preview;
    }

    private static string ResolveTitle(string? fallbackTitle, IReadOnlyList<MessageRecord> messages)
    {
        var latestPrompt = messages
            .Where(message => message.Status == MessageStatus.Completed && message.Role == MessageRole.User)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Select(message => NormalizeText(message.MarkdownContent))
            .FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt));
        if (!string.IsNullOrWhiteSpace(latestPrompt))
        {
            return latestPrompt.Length > 64 ? latestPrompt[..64] + "..." : latestPrompt;
        }

        var resolved = string.IsNullOrWhiteSpace(fallbackTitle) ? "SelfClaw" : fallbackTitle.Trim();
        return resolved.Length > 64 ? resolved[..64] + "..." : resolved;
    }

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
