using System.Text;
using System.Windows;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void OnToolApprovalRequested(ToolApprovalRequest request)
    {
        if (System.Windows.Application.Current?.Dispatcher is Dispatcher dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(new Action(() => OnToolApprovalRequested(request)), DispatcherPriority.Background);
            return;
        }

        var title = ResolveNotificationTitle(request.ConversationId, request.DisplayName);
        _desktopNotificationService.ShowToolApproval(
            title,
            request.Description,
            () => ApproveToolExecutionAsync(request.ToolExecutionId),
            () => RejectToolExecutionAsync(request.ToolExecutionId));
    }

    private void PublishConversationCompletedNotification(ConversationRecord conversation)
    {
        if (conversation.Mode is not (ConversationMode.Programming or ConversationMode.Team))
        {
            return;
        }

        var title = ResolveNotificationTitle(conversation.Id, conversation.Title);
        var message = BuildConversationCompletedMessage(conversation);
        _desktopNotificationService.ShowConversationCompleted(
            title,
            message,
            prompt => ContinueConversationFromNotificationAsync(conversation.Id, prompt));
    }

    private string BuildConversationCompletedMessage(ConversationRecord conversation)
    {
        var modeMessage = conversation.Mode == ConversationMode.Team
            ? "Team session completed."
            : "Programming session completed.";
        var preview = BuildConversationPreview();

        return string.IsNullOrWhiteSpace(preview)
            ? modeMessage
            : $"{modeMessage}\n{preview}";
    }

    private string BuildConversationPreview()
    {
        var latestMessage = _messages
            .Where(message => message.Status == MessageStatus.Completed && message.Role == MessageRole.Assistant)
            .OrderByDescending(message => message.CreatedAtUtc)
            .FirstOrDefault()
            ?? _messages
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

    private string ResolveNotificationTitle(Guid? conversationId, string? fallbackTitle)
    {
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

    private async Task ContinueConversationFromNotificationAsync(Guid conversationId, string prompt)
    {
        var normalizedPrompt = prompt.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return;
        }

        var conversation = _allConversations.FirstOrDefault(item => item.Id == conversationId);
        if (conversation is null)
        {
            StatusText = "This conversation is no longer available.";
            PublishShell(false);
            return;
        }

        if (SelectedConversation?.Id != conversation.Id)
        {
            SelectedConversationMode = conversation.Mode;
            SelectedWorkspaceRoot = conversation.WorkspaceRootId is Guid workspaceRootId
                ? WorkspaceRoots.FirstOrDefault(root => root.Id == workspaceRootId)
                : null;

            ApplyConversationFilter(conversation.Id);
            await LoadConversationAsync(conversation);
        }

        await SubmitPromptAsync(normalizedPrompt);
    }
}
