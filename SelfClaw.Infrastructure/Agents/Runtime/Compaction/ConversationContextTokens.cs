using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime.Compaction;

internal static class ConversationContextTokens
{
    public const int MessageOverheadTokens = 4;

    private const int ImageMetadataTokens = 32;

    public static bool IsCoveredBySummary(MessageRecord message, ConversationContextSummaryRecord summary)
    {
        if (summary.CoveredThroughMessageCreatedAtUtc is not DateTimeOffset coveredAt)
        {
            return false;
        }

        return message.CreatedAtUtc <= coveredAt;
    }

    public static int EstimateEffectiveTokens(
        ConversationContextSummaryRecord? summary,
        IReadOnlyList<MessageRecord> messages)
    {
        var total = messages.Sum(EstimateMessageTokens);
        if (summary is not null && !string.IsNullOrWhiteSpace(summary.SummaryMarkdown))
        {
            total += EstimateTextTokens(summary.SummaryMarkdown) + MessageOverheadTokens;
        }

        return Math.Max(0, total);
    }

    public static int EstimateMeasuredEffectiveTokens(
        ConversationContextSummaryRecord? summary,
        IReadOnlyList<MessageRecord> promptMessages)
    {
        var measuredIndex = -1;
        for (var index = promptMessages.Count - 1; index >= 0; index--)
        {
            if (promptMessages[index].Role == MessageRole.Assistant && promptMessages[index].InputTokens is > 0)
            {
                measuredIndex = index;
                break;
            }
        }

        if (measuredIndex < 0)
        {
            return 0;
        }

        var measuredMessage = promptMessages[measuredIndex];
        if (summary is not null && IsCoveredBySummary(measuredMessage, summary))
        {
            return 0;
        }

        var total = Math.Max(0L, measuredMessage.InputTokens!.Value);
        total += measuredMessage.OutputTokens is > 0
            ? measuredMessage.OutputTokens.Value
            : EstimateMessageTokens(measuredMessage);

        for (var index = measuredIndex + 1; index < promptMessages.Count; index++)
        {
            var message = promptMessages[index];
            if (summary is not null && IsCoveredBySummary(message, summary))
            {
                continue;
            }

            total += EstimateMessageTokens(message);
        }

        return total > int.MaxValue ? int.MaxValue : (int)Math.Max(0L, total);
    }

    public static int EstimateMessageTokens(MessageRecord message)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;
        var attachmentTokens = message.Attachments?.Count * ImageMetadataTokens ?? 0;
        var speakerTokens = EstimateTextTokens(message.AgentName) + EstimateTextTokens(message.AgentRole);
        return MessageOverheadTokens + EstimateTextTokens(content) + attachmentTokens + speakerTokens;
    }

    public static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
    }

    public static string TruncateToEstimatedTokens(string text, int maxTokens)
    {
        if (maxTokens <= 0)
        {
            return string.Empty;
        }

        var maxChars = Math.Max(1, maxTokens * 4);
        if (text.Length <= maxChars)
        {
            return text;
        }

        const string suffix = "\n\n[Compacted summary truncated to fit model_context_window.]";
        var contentChars = Math.Max(1, maxChars - suffix.Length);
        return text[..contentChars].TrimEnd() + suffix;
    }
}
