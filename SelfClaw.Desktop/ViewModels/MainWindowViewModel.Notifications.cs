using System.Text;
using System.Windows;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string NotificationToolAnchorPrefix = "<!--selfclaw:tool:";

    private void OnToolApprovalRequested(ToolApprovalRequest request)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => OnToolApprovalRequested(request)), DispatcherPriority.Background);
            return;
        }

        var title = ResolveNotificationTitle(request.ConversationId, request.DisplayName);
        _desktopNotificationService.ShowToolApproval(
            request.ToolExecutionId,
            request.ConversationId,
            title,
            request.Description);
    }

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
        var message = BuildConversationCompletedMessage(conversation, messages);
        _desktopNotificationService.ShowConversationCompleted(
            conversation.Id,
            title,
            message);
    }

    private string BuildConversationCompletedMessage(
        ConversationRecord conversation,
        IReadOnlyList<MessageRecord> messages)
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

    public async Task OpenConversationFromNotificationAsync(Guid conversationId)
    {
        var conversation = await ResolveConversationForNotificationAsync(conversationId);
        if (conversation is null || conversation.Mode != ConversationMode.Programming)
        {
            PublishShell(false);
            return;
        }

        if (SelectedConversation?.Id == conversation.Id)
        {
            return;
        }

        SelectWorkspaceRoot(
            conversation.WorkspaceRootId is Guid workspaceRootId
                ? _workspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
                : null,
            publishShell: false);

        ApplyConversationFilter(conversation.Id);
        await LoadConversationAsync(conversation);
    }

    private async Task<ConversationRecord?> ResolveConversationForNotificationAsync(Guid conversationId)
    {
        var conversation = _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is not null)
        {
            return conversation;
        }

        await ReloadConversationsAsync();
        return _allConversations.FirstOrDefault(item => item.Id == conversationId);
    }
}
