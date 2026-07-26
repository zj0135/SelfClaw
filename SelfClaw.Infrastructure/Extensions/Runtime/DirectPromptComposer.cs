using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

internal sealed class DirectPromptComposer
{
    public IReadOnlyList<ChatMessage> BuildMessages(
        IReadOnlyList<MessageRecord> messages,
        string agentInstructions,
        IReadOnlyList<string> systemInstructions,
        IReadOnlyDictionary<Guid, string> messageAdjustments)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(systemInstructions);
        ArgumentNullException.ThrowIfNull(messageAdjustments);
        var result = new List<ChatMessage>();
        var systemSections = new[] { agentInstructions }
            .Concat(systemInstructions)
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToArray();
        if (systemSections.Length > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, string.Join("\n\n", systemSections)));
        }

        foreach (var message in messages)
        {
            if (message.Status is MessageStatus.Failed or MessageStatus.Cancelled)
            {
                continue;
            }

            var markdown = messageAdjustments.GetValueOrDefault(message.Id) ?? message.MarkdownContent;
            if (string.IsNullOrEmpty(markdown))
            {
                continue;
            }

            ChatRole? role = message.Role switch
            {
                MessageRole.User => ChatRole.User,
                MessageRole.Assistant => ChatRole.Assistant,
                _ => null
            };
            if (role is ChatRole chatRole)
            {
                result.Add(new ChatMessage(chatRole, markdown));
            }
        }

        return result;
    }
}
