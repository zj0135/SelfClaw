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
        total += EstimateSummaryTokens(summary);

        return Math.Max(0, total);
    }

    /// <summary>
    /// Estimates the effective token count for messages using the provider-reported InputTokens
    /// from assistant messages, with system/tool overhead subtracted via delta calibration.
    /// </summary>
    /// <remarks>
    /// The API-reported InputTokens includes the entire request payload: system prompt,
    /// tool definitions, context providers, AND message history. Since the compaction trigger
    /// limit is designed as a budget for message content only, we need to exclude the fixed
    /// non-message overhead (system prompt + tools).
    ///
    /// Strategy: Two-point delta calibration.
    /// If two assistant messages have InputTokens, the overhead cancels in the difference:
    ///   ratio = (InputTokens_B - InputTokens_A) / (localEstimate_B - localEstimate_A)
    /// This gives a true calibration ratio without needing to know the overhead.
    ///
    /// If only one measurement exists, we infer overhead = InputTokens - localEstimate
    /// (for messages BEFORE the assistant turn, per P1 fix). The ratio defaults to 1.0
    /// in this case, but we still correctly subtract the overhead from the reported value.
    ///
    /// P1 fix: InputTokens on an assistant message represents the prompt tokens that
    /// produced that response. It includes all messages BEFORE the assistant message
    /// but NOT the assistant message itself. So we use index &lt; measuredIndex.
    /// </remarks>
    public static int EstimateMeasuredEffectiveTokens(
        ConversationContextSummaryRecord? summary,
        IReadOnlyList<MessageRecord> promptMessages)
    {
        // Find the two most recent assistant messages with InputTokens for delta calibration.
        var measuredIndexB = -1;
        var measuredIndexA = -1;
        for (var index = promptMessages.Count - 1; index >= 0; index--)
        {
            if (promptMessages[index].Role == MessageRole.Assistant && promptMessages[index].InputTokens is > 0)
            {
                if (summary is not null && IsCoveredBySummary(promptMessages[index], summary))
                {
                    continue;
                }

                if (measuredIndexB < 0)
                {
                    measuredIndexB = index;
                }
                else
                {
                    measuredIndexA = index;
                    break;
                }
            }
        }

        if (measuredIndexB < 0)
        {
            return 0;
        }

        var measuredMessageB = promptMessages[measuredIndexB];
        // Compute calibration ratio using delta between two measurement points.
        var calibrationRatio = 1.0;

        if (measuredIndexA >= 0)
        {
            // Two-point delta: overhead cancels out.
            // InputTokens represents the prompt BEFORE the assistant response (P1 fix: index < measuredIndex).
            var inputTokensB = Math.Max(0L, measuredMessageB.InputTokens!.Value);
            var inputTokensA = Math.Max(0L, promptMessages[measuredIndexA].InputTokens!.Value);

            // Local estimate for messages in [0, measuredIndexB) minus messages in [0, measuredIndexA)
            // = messages in [measuredIndexA, measuredIndexB)
            var localEstimateDelta = 0L;
            for (var index = measuredIndexA; index < measuredIndexB; index++)
            {
                var message = promptMessages[index];
                if (summary is not null && IsCoveredBySummary(message, summary))
                {
                    continue;
                }

                localEstimateDelta += EstimateMessageTokens(message);
            }

            var inputDelta = inputTokensB - inputTokensA;
            if (localEstimateDelta > 0 && inputDelta > 0)
            {
                calibrationRatio = (double)inputDelta / localEstimateDelta;
                // Sanity bound: ratio should be between 0.5 and 3.0.
                calibrationRatio = Math.Clamp(calibrationRatio, 0.5, 3.0);
            }
        }
        else
        {
            // Single measurement point: can't determine ratio independently.
            // Infer overhead = InputTokens - localEstimate(messages before the assistant turn).
            // P1 fix: InputTokens covers messages BEFORE the assistant message, not including it.
            var reportedInputTokens = Math.Max(0L, measuredMessageB.InputTokens!.Value);
            var localEstimateBeforeB = EstimateSummaryTokens(summary);
            for (var index = 0; index < measuredIndexB; index++)
            {
                var message = promptMessages[index];
                if (summary is not null && IsCoveredBySummary(message, summary))
                {
                    continue;
                }

                localEstimateBeforeB += EstimateMessageTokens(message);
            }

            if (localEstimateBeforeB <= 0L)
            {
                return 0;
            }

            var inferredOverhead = Math.Max(0L, reportedInputTokens - localEstimateBeforeB);

            // If overhead is unreasonably large (>95%), the local estimate is too inaccurate.
            if (inferredOverhead > reportedInputTokens * 95 / 100)
            {
                return 0;
            }

            // With single point, ratio stays 1.0 (we can't calibrate, but overhead is subtracted).
        }

        // Compute the local estimate for ALL current uncovered messages and apply the ratio.
        var localEstimateForAllMessages = 0L;
        for (var index = 0; index < promptMessages.Count; index++)
        {
            var message = promptMessages[index];
            if (summary is not null && IsCoveredBySummary(message, summary))
            {
                continue;
            }

            localEstimateForAllMessages += EstimateMessageTokens(message);
        }

        // Apply calibration to get a more accurate message-only token count.
        var calibratedTotal = (long)Math.Ceiling(localEstimateForAllMessages * calibrationRatio);

        // P1-output fix: The latest measured assistant's OutputTokens is part of the next
        // round's context but is NOT included in its own InputTokens. If we have real
        // OutputTokens, use them instead of the local estimate (which is already in
        // localEstimateForAllMessages and thus in calibratedTotal).
        if (measuredMessageB.OutputTokens is > 0)
        {
            var estimatedOutput = (long)Math.Ceiling(EstimateMessageTokens(measuredMessageB) * calibrationRatio);
            var realOutput = measuredMessageB.OutputTokens.Value;
            var outputDelta = realOutput - estimatedOutput;
            if (outputDelta > 0)
            {
                calibratedTotal += outputDelta;
            }
        }

        calibratedTotal += EstimateSummaryTokens(summary);

        return calibratedTotal > int.MaxValue ? int.MaxValue : (int)Math.Max(0L, calibratedTotal);
    }

    private static int EstimateSummaryTokens(ConversationContextSummaryRecord? summary)
    {
        return summary is null || string.IsNullOrWhiteSpace(summary.SummaryMarkdown)
            ? 0
            : EstimateTextTokens(summary.SummaryMarkdown) + MessageOverheadTokens;
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
