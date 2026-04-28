using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class ConversationContextCompactionService : IConversationContextCompactionService
{
    private const int MessageOverheadTokens = 4;
    private const int ImageMetadataTokens = 32;
    private const int MinimumRecentMessages = 6;
    private const double RecentTailFraction = 0.25d;
    private const string CompactorName = "ContextCompactor";
    private const string CompactorDescription = "Summarizes old conversation history for future chat context.";
    private const string SyntheticSummaryPrefix = "Automatic compacted history summary. Use this as background context for earlier conversation turns; the original transcript remains stored separately.";

    private readonly IConversationRepository _conversationRepository;
    private readonly IAgentExecutionService _agentExecutionService;
    private readonly ILogger<ConversationContextCompactionService> _logger;

    public ConversationContextCompactionService(
        IConversationRepository conversationRepository,
        IAgentExecutionService agentExecutionService,
        ILogger<ConversationContextCompactionService>? logger = null)
    {
        _conversationRepository = conversationRepository;
        _agentExecutionService = agentExecutionService;
        _logger = logger ?? NullLogger<ConversationContextCompactionService>.Instance;
    }

    public async Task<IReadOnlyList<MessageRecord>> PrepareMessagesAsync(
        Guid conversationId,
        ProviderProfile profile,
        string apiKey,
        IReadOnlyList<MessageRecord> messages,
        int modelContextWindow,
        int modelAutoCompactTokenLimit,
        CancellationToken cancellationToken = default)
    {
        var contextWindow = Math.Max(1, modelContextWindow);
        var autoCompactLimit = modelAutoCompactTokenLimit < 0 ? 0 : Math.Min(modelAutoCompactTokenLimit, contextWindow);
        var triggerLimit = autoCompactLimit > 0 ? autoCompactLimit : contextWindow;
        var promptMessages = messages
            .Where(ShouldIncludeInPrompt)
            .OrderBy(message => message.CreatedAtUtc)
            .ToArray();

        if (promptMessages.Length == 0)
        {
            return messages;
        }

        var latestUserMessage = promptMessages.LastOrDefault(message => message.Role == MessageRole.User);
        if (latestUserMessage is not null && EstimateMessageTokens(latestUserMessage) > contextWindow)
        {
            throw new InvalidOperationException(
                "The latest user message is larger than the configured model_context_window and cannot be sent safely.");
        }

        var existingSummary = await _conversationRepository.GetConversationContextSummaryAsync(conversationId, cancellationToken);
        var uncoveredMessages = existingSummary is null
            ? promptMessages
            : promptMessages.Where(message => !IsCoveredBySummary(message, existingSummary)).ToArray();
        var localEffectiveTokenEstimate = EstimateEffectiveTokens(existingSummary, uncoveredMessages);
        var measuredEffectiveTokenEstimate = EstimateMeasuredEffectiveTokens(existingSummary, promptMessages);
        var effectiveTokenEstimate = Math.Max(localEffectiveTokenEstimate, measuredEffectiveTokenEstimate);

        if (effectiveTokenEstimate < triggerLimit)
        {
            return existingSummary is null
                ? messages
                : BuildPreparedMessages(conversationId, existingSummary, uncoveredMessages, contextWindow);
        }

        var tailCandidates = existingSummary is null ? promptMessages : uncoveredMessages;
        var recentTail = SelectRecentTail(tailCandidates, triggerLimit);
        var recentTailIds = recentTail.Select(message => message.Id).ToHashSet();
        var historyToSummarize = promptMessages
            .Where(message => !recentTailIds.Contains(message.Id))
            .Where(message => existingSummary is null || !IsCoveredBySummary(message, existingSummary))
            .ToArray();

        if (historyToSummarize.Length == 0 && existingSummary is not null)
        {
            return BuildPreparedMessages(conversationId, existingSummary, recentTail, contextWindow);
        }

        ConversationContextSummaryRecord? nextSummary;
        try
        {
            nextSummary = await CompactHistoryAsync(
                conversationId,
                profile,
                apiKey,
                existingSummary,
                historyToSummarize,
                contextWindow,
                triggerLimit,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (effectiveTokenEstimate <= contextWindow)
            {
                _logger.LogWarning(
                    exception,
                    "Context compaction failed below hard context window; continuing without a new summary. ConversationId={ConversationId}",
                    conversationId);
                return existingSummary is null
                    ? messages
                    : BuildPreparedMessages(conversationId, existingSummary, uncoveredMessages, contextWindow);
            }

            _logger.LogError(
                exception,
                "Context compaction failed above hard context window. ConversationId={ConversationId}",
                conversationId);
            throw new InvalidOperationException(
                "The conversation exceeded model_context_window and automatic context compaction failed. Reduce the current prompt or try again.",
                exception);
        }

        if (nextSummary is null)
        {
            if (effectiveTokenEstimate <= contextWindow)
            {
                return existingSummary is null
                    ? messages
                    : BuildPreparedMessages(conversationId, existingSummary, uncoveredMessages, contextWindow);
            }

            throw new InvalidOperationException(
                "The conversation exceeded model_context_window and automatic context compaction produced no summary.");
        }

        var fittedSummary = FitSummaryToWindow(nextSummary, recentTail, contextWindow);
        await _conversationRepository.UpsertConversationContextSummaryAsync(fittedSummary, cancellationToken);

        return BuildPreparedMessages(conversationId, fittedSummary, recentTail, contextWindow);
    }

    private async Task<ConversationContextSummaryRecord?> CompactHistoryAsync(
        Guid conversationId,
        ProviderProfile profile,
        string apiKey,
        ConversationContextSummaryRecord? existingSummary,
        IReadOnlyList<MessageRecord> historyToSummarize,
        int contextWindow,
        int triggerLimit,
        CancellationToken cancellationToken)
    {
        if (historyToSummarize.Count == 0 && existingSummary is null)
        {
            return null;
        }

        var runningSummary = existingSummary?.SummaryMarkdown;
        var batches = CreateBatches(historyToSummarize, Math.Max(1_000, (int)Math.Floor(Math.Min(contextWindow, triggerLimit) * 0.6d)));
        if (batches.Count == 0 && !string.IsNullOrWhiteSpace(runningSummary))
        {
            return existingSummary;
        }

        foreach (var batch in batches)
        {
            runningSummary = await SummarizeBatchAsync(
                profile,
                apiKey,
                runningSummary,
                batch,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(runningSummary))
        {
            runningSummary = existingSummary?.SummaryMarkdown;
        }

        if (string.IsNullOrWhiteSpace(runningSummary))
        {
            return null;
        }

        var coveredThrough = historyToSummarize.LastOrDefault();
        var now = DateTimeOffset.UtcNow;
        return new ConversationContextSummaryRecord(
            conversationId,
            runningSummary.Trim(),
            coveredThrough?.Id ?? existingSummary?.CoveredThroughMessageId,
            coveredThrough?.CreatedAtUtc ?? existingSummary?.CoveredThroughMessageCreatedAtUtc,
            Math.Max(existingSummary?.SourceTokenEstimate ?? 0, 0) + historyToSummarize.Sum(EstimateMessageTokens),
            EstimateTextTokens(runningSummary),
            existingSummary?.CreatedAtUtc ?? now,
            now);
    }

    private async Task<string> SummarizeBatchAsync(
        ProviderProfile profile,
        string apiKey,
        string? existingSummary,
        IReadOnlyList<MessageRecord> batch,
        CancellationToken cancellationToken)
    {
        var payload = BuildCompactionPayload(existingSummary, batch);
        var result = await _agentExecutionService.RunAsync(
            new AgentExecutionRequest(
                profile,
                apiKey,
                CompactorName,
                CompactorDescription,
                BuildCompactionInstructions(),
                [new ChatMessage(ChatRole.User, payload)],
                [],
                ContextProviders: null,
                EnableReasoning: false),
            onTextDelta: null,
            cancellationToken);

        return result.FinalMarkdown;
    }

    private static string BuildCompactionInstructions()
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

    private static string BuildCompactionPayload(string? existingSummary, IReadOnlyList<MessageRecord> batch)
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

    private static IReadOnlyList<IReadOnlyList<MessageRecord>> CreateBatches(
        IReadOnlyList<MessageRecord> messages,
        int batchTokenLimit)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var batches = new List<IReadOnlyList<MessageRecord>>();
        var current = new List<MessageRecord>();
        var currentTokens = 0;
        foreach (var message in messages)
        {
            var messageTokens = EstimateMessageTokens(message);
            if (current.Count > 0 && currentTokens + messageTokens > batchTokenLimit)
            {
                batches.Add(current.ToArray());
                current.Clear();
                currentTokens = 0;
            }

            current.Add(message);
            currentTokens += messageTokens;
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }

    private static IReadOnlyList<MessageRecord> SelectRecentTail(
        IReadOnlyList<MessageRecord> messages,
        int triggerLimit)
    {
        var targetTailTokens = Math.Max(1, (int)Math.Floor(triggerLimit * RecentTailFraction));
        var selected = new List<MessageRecord>();
        var selectedTokens = 0;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (selected.Count >= MinimumRecentMessages && selectedTokens >= targetTailTokens)
            {
                break;
            }

            selected.Add(message);
            selectedTokens += EstimateMessageTokens(message);
        }

        selected.Reverse();
        return selected;
    }

    private static IReadOnlyList<MessageRecord> TrimTailToFit(
        ConversationContextSummaryRecord? summary,
        IReadOnlyList<MessageRecord> tail,
        int contextWindow)
    {
        var trimmed = tail.ToList();
        var latestUserMessage = trimmed.LastOrDefault(message => message.Role == MessageRole.User);
        while (EstimateEffectiveTokens(summary, trimmed) > contextWindow && trimmed.Count > 1)
        {
            var removeIndex = trimmed.FindIndex(message => latestUserMessage is null || message.Id != latestUserMessage.Id);
            if (removeIndex < 0)
            {
                break;
            }

            trimmed.RemoveAt(removeIndex);
        }

        return trimmed;
    }

    private static ConversationContextSummaryRecord FitSummaryToWindow(
        ConversationContextSummaryRecord summary,
        IReadOnlyList<MessageRecord> tail,
        int contextWindow)
    {
        var fittedTail = TrimTailToFit(summary, tail, contextWindow);
        var tailTokens = fittedTail.Sum(EstimateMessageTokens);
        var availableSummaryTokens = Math.Max(1, contextWindow - tailTokens - MessageOverheadTokens);
        if (EstimateTextTokens(summary.SummaryMarkdown) <= availableSummaryTokens)
        {
            return summary;
        }

        var truncated = TruncateToEstimatedTokens(summary.SummaryMarkdown, availableSummaryTokens);
        return summary with
        {
            SummaryMarkdown = truncated,
            SummaryTokenEstimate = EstimateTextTokens(truncated),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<MessageRecord> BuildPreparedMessages(
        Guid conversationId,
        ConversationContextSummaryRecord? summary,
        IReadOnlyList<MessageRecord> tail,
        int contextWindow)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.SummaryMarkdown))
        {
            return TrimTailToFit(null, tail, contextWindow);
        }

        var trimmedTail = TrimTailToFit(summary, tail, contextWindow);
        var prepared = new List<MessageRecord>(trimmedTail.Count + 1)
        {
            CreateSyntheticSummaryMessage(conversationId, summary, trimmedTail, contextWindow)
        };
        prepared.AddRange(trimmedTail);
        return prepared;
    }

    private static MessageRecord CreateSyntheticSummaryMessage(
        Guid conversationId,
        ConversationContextSummaryRecord summary,
        IReadOnlyList<MessageRecord> tail,
        int contextWindow)
    {
        var tailTokens = tail.Sum(EstimateMessageTokens);
        var availableSummaryTokens = Math.Max(1, contextWindow - tailTokens - MessageOverheadTokens);
        var summaryMarkdown = EstimateTextTokens(summary.SummaryMarkdown) <= availableSummaryTokens
            ? summary.SummaryMarkdown
            : TruncateToEstimatedTokens(summary.SummaryMarkdown, availableSummaryTokens);
        var content = $"{SyntheticSummaryPrefix}\n\n{summaryMarkdown.Trim()}";
        return new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.System,
            content,
            MessageStatus.Completed,
            summary.UpdatedAtUtc,
            summary.UpdatedAtUtc);
    }

    private static bool IsCoveredBySummary(MessageRecord message, ConversationContextSummaryRecord summary)
    {
        if (summary.CoveredThroughMessageCreatedAtUtc is not DateTimeOffset coveredAt)
        {
            return false;
        }

        return message.CreatedAtUtc <= coveredAt;
    }

    private static int EstimateEffectiveTokens(
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

    private static int EstimateMeasuredEffectiveTokens(
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

    private static int EstimateMessageTokens(MessageRecord message)
    {
        var content = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(message.MarkdownContent).ContentMarkdown
            : message.MarkdownContent;
        var attachmentTokens = message.Attachments?.Count * ImageMetadataTokens ?? 0;
        var speakerTokens = EstimateTextTokens(message.AgentName) + EstimateTextTokens(message.AgentRole);
        return MessageOverheadTokens + EstimateTextTokens(content) + attachmentTokens + speakerTokens;
    }

    private static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
    }

    private static string TruncateToEstimatedTokens(string text, int maxTokens)
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

    private static bool ShouldIncludeInPrompt(MessageRecord message)
    {
        if (message.Status == MessageStatus.Failed)
        {
            return false;
        }

        if (message.Role == MessageRole.Assistant)
        {
            var segments = AssistantMessageSegmenter.Split(message.MarkdownContent);
            return !string.IsNullOrWhiteSpace(segments.ContentMarkdown);
        }

        return true;
    }
}
