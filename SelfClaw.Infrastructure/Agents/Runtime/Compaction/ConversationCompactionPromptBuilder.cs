using System.Text;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime.Compaction;

internal static class ConversationCompactionPromptBuilder
{
    public static string BuildInstructions()
        => """
Summarize older conversation history for a future assistant prompt.

Requirements:
- Preserve user goals, decisions, constraints, unresolved questions, important file paths, commands, tool outcomes mentioned in assistant content, and current task state.
- Keep named agents, roles, channel context, and plan decisions when present.
- Do not invent facts or claim tool access.
- Do not include hidden reasoning or transcript mechanics.
- Prefer compact Markdown with short sections.
- Target roughly 800-1200 tokens unless the source requires more.
""";

    public static string BuildPayload(string? existingSummary, IReadOnlyList<MessageRecord> batch)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            builder.AppendLine("Existing compacted summary:");
            builder.AppendLine(existingSummary.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("New conversation history to merge into the compacted summary:");
        foreach (var message in batch)
        {
            builder.AppendLine();
            builder.AppendLine(FormatMessageForSummary(message));
        }

        return builder.ToString();
    }

    private static string FormatMessageForSummary(MessageRecord message)
    {
        var role = message.Role.ToString();
        var speaker = message.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(message.AgentName)
            ? string.IsNullOrWhiteSpace(message.AgentRole)
                ? message.AgentName
                : $"{message.AgentName} ({message.AgentRole})"
            : role;

        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;

        var builder = new StringBuilder();
        builder.AppendLine($"[{message.CreatedAtUtc:O}] {speaker}:");
        if (!string.IsNullOrWhiteSpace(content))
        {
            builder.AppendLine(content.Trim());
        }

        if (message.Attachments is { Count: > 0 } attachments)
        {
            foreach (var attachment in attachments)
            {
                builder.AppendLine(
                    $"- Attachment metadata: {attachment.Kind}, {attachment.FileName}, {attachment.MediaType}, {attachment.ByteLength} bytes");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
