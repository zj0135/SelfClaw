using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Conversations;

/// <summary>
/// Turns the first user message of a conversation into a short title with the conversation's own model.
/// Failures degrade to "no title": the caller keeps the prompt-preview title, so a failed generation can
/// neither leave the conversation untitled nor affect the turn that is running alongside it.
/// </summary>
internal sealed class ConversationTitleGenerator : IConversationTitleGenerator
{
    /// <summary>Matches the transcript's prompt-preview truncation, so fallback and generated titles read alike.</summary>
    private const int MaximumTitleLength = 48;

    // Only the head of a long first message shapes a title; the tail would add cost without changing it.
    private const int MaximumPromptLength = 4_000;

    // The cap buys the model's thinking, not just the title: a reasoning provider counts the reasoning and
    // the answer against one budget, and this model spends anywhere from 100 to 800 output tokens before the
    // first title character. At 256 a heavier reasoning pass spent the whole cap and the provider answered
    // finish_reason=length with no text at all, which the caller can only read as "no title" (2026-11-09 log).
    // 1024 also stays inside the 20 s generation timeout at the throughput this provider serves.
    private const int TitleOutputTokens = 1_024;

    private const string TitleInstruction =
        "You write titles for conversations. The message that follows is the first user message of a conversation. " +
        "Reply with a single-line title, at most 48 characters, in the same language as that message. " +
        "Plain text only: no quotes, no markdown, no trailing punctuation. Reply with the title only.";

    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly ILogger<ConversationTitleGenerator> _logger;

    internal ConversationTitleGenerator(
        IAiChatClientFactory chatClientFactory,
        ILogger<ConversationTitleGenerator>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _logger = logger ?? NullLogger<ConversationTitleGenerator>.Instance;
    }

    public async Task<string?> GenerateTitleAsync(string prompt, Guid? modelProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        try
        {
            var preparation = await _chatClientFactory.PrepareAsync(modelProfileId, cancellationToken).ConfigureAwait(false);
            using var lease = _chatClientFactory.Create(preparation, new AiChatClientPipelineOptions([]));
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, TitleInstruction),
                new(ChatRole.User, Truncate(prompt))
            };
            // Only the output cap is set: sampling and the profile's reasoning effort (思考等级) stay out of
            // this call so the provider's defaults apply, exactly like DirectContextCompressor.
            var options = new ChatOptions { MaxOutputTokens = TitleOutputTokens };
            // Streaming is the path every provider serves correctly: some OpenAI-compatible endpoints answer a
            // non-streaming request with an empty choices array, which the SDK dereferences unguarded.
            var response = await lease.Client
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync(cancellationToken)
                .ConfigureAwait(false);
            var title = Sanitize(response.Text);
            if (title is null)
            {
                // "The title never changed" is the whole symptom, so an empty answer has to say why it was
                // empty: a truncated thinking block and a model that silently answered nothing look alike.
                _logger.LogWarning(
                    "Conversation title generation produced no usable text; keeping the existing title. " +
                    "FinishReason={FinishReason}, OutputTokens={OutputTokens}, Budget={Budget}.",
                    response.FinishReason,
                    response.Usage?.OutputTokenCount,
                    TitleOutputTokens);
            }

            return title;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The prompt is the user's own content: diagnostics never echo it.
            _logger.LogWarning(exception, "Conversation title generation failed; keeping the existing title.");
            return null;
        }
    }

    private static string Truncate(string prompt)
        => prompt.Length > MaximumPromptLength ? prompt[..MaximumPromptLength] : prompt;

    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var title = TrimWrapping(raw.ReplaceLineEndings(" ").Trim());
        title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > MaximumTitleLength) title = title[..MaximumTitleLength];
        return title.Length == 0 ? null : title;
    }

    private static string TrimWrapping(string title)
    {
        while (title.Length >= 2 && IsWrappingPair(title[0], title[^1]))
        {
            title = title[1..^1].Trim();
        }

        return title;
    }

    private static bool IsWrappingPair(char first, char last)
        => (first, last) is ('"', '"') or ('\'', '\'') or ('`', '`') or ('“', '”') or ('‘', '’') or ('「', '」') or ('《', '》');
}
