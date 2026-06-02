using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime.Orchestration;

public sealed partial class SelfClawAgentChatRuntime
{
    private static IReadOnlyList<ChatMessage> BuildPromptMessages(
        IReadOnlyList<MessageRecord> messages,
        bool includeAssistantSpeakerPrefix = true)
        => messages
            .Where(ShouldIncludeInPrompt)
            .Select(message => MapMessage(message, includeAssistantSpeakerPrefix))
            .ToArray();

    private static MessageRecord CreateAssistantMessage(
        Guid conversationId,
        Guid messageId,
        Guid? agentId,
        string agentName,
        string agentRole)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            messageId,
            conversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            agentId,
            agentName,
            agentRole);
    }


    private static bool ShouldIncludeInPrompt(MessageRecord message)
    {
        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            if (string.IsNullOrWhiteSpace(segments.ContentMarkdown))
            {
                return false;
            }
        }

        return message.Status != MessageStatus.Failed;
    }


    private static ChatMessage MapMessage(
        MessageRecord message,
        bool includeAssistantSpeakerPrefix)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;

        if (includeAssistantSpeakerPrefix &&
            message.Role == MessageRole.Assistant &&
            !string.IsNullOrWhiteSpace(message.AgentName))
        {
            var speaker = string.IsNullOrWhiteSpace(message.AgentRole)
                ? message.AgentName
                : $"{message.AgentName} ({message.AgentRole})";
            content = $"[{speaker}]\n{content}";
        }

        if (message.Role == MessageRole.User && message.Attachments is { Count: > 0 } attachments)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrWhiteSpace(content))
            {
                contents.Add(new TextContent(content));
            }

            foreach (var attachment in attachments)
            {
                if (attachment.Kind != MessageAttachmentKind.Image ||
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ||
                    !File.Exists(attachment.StoragePath))
                {
                    continue;
                }

                try
                {
                    contents.Add(new DataContent(File.ReadAllBytes(attachment.StoragePath), attachment.MediaType)
                    {
                        Name = attachment.FileName
                    });
                }
                catch
                {
                    // A missing or unreadable old attachment should not make the whole conversation unusable.
                }
            }

            if (contents.Count > 0)
            {
                return new ChatMessage(MapRole(message.Role), contents);
            }
        }

        return new ChatMessage(MapRole(message.Role), content);
    }


    private static ChatRole MapRole(MessageRole role)
        => role switch
        {
            MessageRole.System => ChatRole.System,
            MessageRole.User => ChatRole.User,
            _ => ChatRole.Assistant
        };

}
