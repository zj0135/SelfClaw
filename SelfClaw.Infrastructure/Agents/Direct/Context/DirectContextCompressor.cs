using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// Turns the oldest part of the history into a summary the rest of the turn can replay instead.
/// Failures degrade to "no summary": the caller keeps sending the uncompressed context and reports
/// the oversized request itself, so a failed summary can never silently drop history.
/// </summary>
internal sealed class DirectContextCompressor
{
    /// <summary>Floor for the summary budget: below this a long history cannot be summarized usefully.</summary>
    private const int MinimumSummaryTokens = 512;

    /// <summary>Ceiling for the summary budget: a summary that long no longer buys room back.</summary>
    private const int MaximumSummaryTokens = 4096;

    // The part of a prefix one summary request carries, with the count its output budget is sized from.
    private readonly record struct SummaryChunk(IReadOnlyList<ChatMessage> Messages, int SourceTokens);

    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IAiModelCatalog _modelCatalog;
    private readonly IDirectTokenCounter _tokenCounter;
    private readonly ILogger<DirectContextCompressor> _logger;

    internal DirectContextCompressor(
        IAiChatClientFactory chatClientFactory,
        IAiModelCatalog modelCatalog,
        IDirectTokenCounter tokenCounter,
        ILogger<DirectContextCompressor>? logger = null)
    {
        _chatClientFactory = chatClientFactory;
        _modelCatalog = modelCatalog;
        _tokenCounter = tokenCounter;
        _logger = logger ?? NullLogger<DirectContextCompressor>.Instance;
    }

    internal async Task<DirectCompressionResult?> TryCompressAsync(
        DirectCompressionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiChatClientLease? dedicatedLease = null;
        try
        {
            if (request.ModelProfileId is Guid modelProfileId)
            {
                dedicatedLease = await TryCreateDedicatedLeaseAsync(modelProfileId, cancellationToken).ConfigureAwait(false);
            }

            var summarizer = dedicatedLease ?? request.TurnLease;
            var summary = await SummarizeAsync(request, summarizer, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                _logger.LogWarning(
                    "Context compression returned no summary for conversation {ConversationId} turn {TurnId}.",
                    request.ConversationId,
                    request.TurnId);
                return null;
            }

            return new DirectCompressionResult(
                summary,
                summarizer.Profile.Model,
                // One unit is one stored conversation message, so a unit carrying a tool call plus its
                // result must not inflate the message count the transcript reports.
                request.PrefixUnits.Count,
                request.SourceTokens,
                // The summary is counted with the profile that actually produced it: the dedicated
                // compression model, or the turn model when the policy's model was unusable.
                SummaryTokens: _tokenCounter.CountText(summary, summarizer.Profile),
                UsedFallbackModel: request.ModelProfileId is not null && dedicatedLease is null);
        }
        // A cancellation the turn did not ask for is a failed summary like any other and degrades below.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Context compression failed for conversation {ConversationId} turn {TurnId}.",
                request.ConversationId,
                request.TurnId);
            return null;
        }
        finally
        {
            dedicatedLease?.Dispose();
        }
    }

    /// <summary>
    /// Summarizes the prefix, in several chained calls when it would not fit the summarizer in one: each call
    /// hands its summary to the next as the previous summary. Null means one call produced nothing, and nothing
    /// partial is ever returned, because a summary of half the prefix would silently drop the other half.
    /// </summary>
    private async Task<string?> SummarizeAsync(
        DirectCompressionRequest request,
        AiChatClientLease summarizer,
        CancellationToken cancellationToken)
    {
        var summary = request.PreviousSummary;
        foreach (var chunk in SplitIntoChunks(request, summarizer.Profile))
        {
            summary = await SummarizeChunkAsync(summarizer, summary, chunk, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                return null;
            }
        }

        return summary;
    }

    private async Task<string?> SummarizeChunkAsync(
        AiChatClientLease summarizer,
        string? previousSummary,
        SummaryChunk chunk,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>(chunk.Messages.Count + 2) { new(ChatRole.System, CompressionInstruction.Text) };
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            messages.Add(new ChatMessage(
                ChatRole.User,
                $"<selfclaw-context-summary version=\"1\">\n{previousSummary}\n</selfclaw-context-summary>"));
        }

        // The units already pair tool calls with their results, so replaying their messages keeps the
        // roles and the tool semantics instead of flattening everything into text.
        messages.AddRange(chunk.Messages);
        // Only the output cap is set: the remaining sampling stays at the provider's default instead of
        // re-deriving the model profile's values here.
        var options = new ChatOptions
        {
            MaxOutputTokens = OutputTokenBudget(
                chunk.SourceTokens, _tokenCounter.CountText(previousSummary, summarizer.Profile) ?? 0)
        };

        // Streaming is the path every provider serves correctly: some OpenAI-compatible endpoints answer a
        // non-streaming request with an empty choices array, which the SDK dereferences unguarded (2026-10-18 log).
        var response = await ProviderStreamIdleGuard
            .StreamAsync(summarizer.Client, messages, options, ProviderStreamIdleGuard.DefaultIdleTimeout, cancellationToken)
            .ToChatResponseAsync(cancellationToken)
            .ConfigureAwait(false);
        // A partial answer must never replace the source history, including when an error arrives after text.
        if (response.FinishReason != ChatFinishReason.Stop ||
            response.Messages.SelectMany(message => message.Contents).Any(content => content is ToolCallContent ||
                content is ErrorContent error &&
                !(error.ErrorCode == "Refusal" && string.IsNullOrWhiteSpace(error.Message))))
        {
            throw new InvalidDataException("The context summary did not complete successfully; the original history was retained.");
        }

        var summary = response.Text?.Trim();
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    /// <summary>
    /// A prefix normally goes out in one request. It is split only when it would not fit the summarizer's own
    /// window: a turn that was compacted in-turn stores more than one window of history in a single message,
    /// and a request that large fails, which would leave the conversation unable to start another turn.
    /// </summary>
    private IReadOnlyList<SummaryChunk> SplitIntoChunks(DirectCompressionRequest request, AiModelProfile profile)
    {
        var messages = request.PrefixUnits.SelectMany(unit => PlaceholderizeAttachments(unit.Messages)).ToArray();
        var whole = new SummaryChunk(messages, request.SourceTokens);
        if (ResolveChunkBudget(profile) is not int budget || request.SourceTokens <= budget)
        {
            return [whole];
        }

        var chunks = new List<SummaryChunk>();
        var current = new List<ChatMessage>();
        var currentTokens = 0;
        foreach (var message in messages)
        {
            if (_tokenCounter.CountMessages([message], profile) is not int tokens)
            {
                return [whole];
            }

            // A tool message answers the call right before it, so a chunk never starts with one.
            if (current.Count > 0 && currentTokens + tokens > budget && message.Role != ChatRole.Tool)
            {
                chunks.Add(new SummaryChunk(current, currentTokens));
                current = [];
                currentTokens = 0;
            }

            current.Add(message);
            currentTokens += tokens;
        }

        chunks.Add(new SummaryChunk(current, currentTokens));
        return chunks;
    }

    /// <summary>
    /// How much of a prefix one summary request may carry. Null when the summarizer declares no window or
    /// counting is unavailable: nothing then says what fits, so the prefix goes out whole as it always did.
    /// </summary>
    private int? ResolveChunkBudget(AiModelProfile profile)
    {
        if (AiChatOptions.ResolveContextWindowTokens(profile) is not int window ||
            _tokenCounter.CountText(CompressionInstruction.Text, profile) is not int instructionTokens)
        {
            return null;
        }

        // Room is held back for the answer and for the previous summary every call after the first carries.
        var budget = (int)((window - (2 * MaximumSummaryTokens)) * DirectCompactionPlanner.UsableInputShare) - instructionTokens;
        return budget > 0 ? budget : null;
    }

    /// <summary>
    /// The summarizer needs the meaning of an image, not its pixels: sending the bytes would blow its own
    /// window, and the compaction model need not accept images at all. An image-bearing unit is planned like
    /// any other, so this is the only thing keeping binary content out of the summary request, and it reaches
    /// the images nested inside a native tool result.
    /// </summary>
    private static IEnumerable<ChatMessage> PlaceholderizeAttachments(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var contents = new List<AIContent>(message.Contents.Count);
            var replaced = false;
            foreach (var content in message.Contents)
            {
                var placeholder = Placeholderize(content);
                replaced |= !ReferenceEquals(placeholder, content);
                contents.Add(placeholder);
            }

            // Untouched messages are replayed as the same instance, exactly as before.
            yield return replaced ? new ChatMessage(message.Role, [.. contents]) : message;
        }
    }

    // Returns the same instance when nothing inside changed, so a text-only tool result survives untouched.
    private static AIContent Placeholderize(AIContent content) => content switch
    {
        DataContent data => new TextContent($"[image: {data.Name ?? data.MediaType}]"),
        FunctionResultContent { Result: AIContent nested } result => Placeholderize(result, nested),
        FunctionResultContent { Result: IEnumerable<AIContent> nested } result => Placeholderize(result, nested),
        _ => content
    };

    private static AIContent Placeholderize(FunctionResultContent result, AIContent nested)
    {
        var placeholder = Placeholderize(nested);
        return ReferenceEquals(placeholder, nested) ? result : Rebuild(result, placeholder);
    }

    private static AIContent Placeholderize(FunctionResultContent result, IEnumerable<AIContent> nested)
    {
        var placeholders = nested.Select(Placeholderize).ToArray();
        // Same-instance comparison is the change test: AIContent defines no value equality.
        return placeholders.SequenceEqual(nested) ? result : Rebuild(result, placeholders);
    }

    // The call id and the exception keep the result matched to its call; only the payload is exchanged.
    private static FunctionResultContent Rebuild(FunctionResultContent result, object payload)
        => new(result.CallId, payload) { Exception = result.Exception };

    // A chained call has to carry forward everything the previous summary held before it can add anything, so
    // that summary's size is part of the budget: sized by the new prefix alone, a small prefix would squeeze
    // the whole accumulated summary through the floor.
    private static int OutputTokenBudget(int sourceTokens, int previousSummaryTokens)
        => Math.Clamp(previousSummaryTokens + (sourceTokens / 4), MinimumSummaryTokens, MaximumSummaryTokens);

    private async Task<AiChatClientLease?> TryCreateDedicatedLeaseAsync(
        Guid modelProfileId,
        CancellationToken cancellationToken)
    {
        // The precheck only skips a needless prepare attempt for a model the catalog already knows is unusable.
        // It is not a guarantee: preparing can still fail afterwards (a disabled connection, a credential that
        // no longer decrypts), and that path degrades to the turn lease through the catch below.
        if (!await _modelCatalog.IsModelAvailableAsync(modelProfileId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "Compression model {ModelProfileId} is unavailable; falling back to the turn model.",
                modelProfileId);
            return null;
        }

        try
        {
            var preparation = await _chatClientFactory.PrepareAsync(modelProfileId, cancellationToken).ConfigureAwait(false);
            return _chatClientFactory.Create(preparation, new AiChatClientPipelineOptions([]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A disabled or deleted model must not fail the turn; the caller retries on its own lease.
            _logger.LogWarning(
                exception,
                "Compression model {ModelProfileId} could not be prepared; falling back to the turn model.",
                modelProfileId);
            return null;
        }
    }
}
