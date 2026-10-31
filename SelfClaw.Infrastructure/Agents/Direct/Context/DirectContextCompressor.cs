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
        var messages = CreateSummarizerMessages(request);
        // Only the output cap is set: the remaining sampling stays at the provider's default instead of
        // re-deriving the model profile's values here.
        var options = new ChatOptions { MaxOutputTokens = OutputTokenBudget(request.SourceTokens) };

        AiChatClientLease? dedicatedLease = null;
        try
        {
            var usedFallbackModel = false;
            IChatClient client;
            AiModelProfile producedProfile;
            if (request.ModelProfileId is Guid modelProfileId &&
                await TryCreateDedicatedLeaseAsync(modelProfileId, cancellationToken).ConfigureAwait(false) is { } lease)
            {
                dedicatedLease = lease;
                client = lease.Client;
                producedProfile = lease.Profile;
            }
            else
            {
                usedFallbackModel = request.ModelProfileId is not null;
                if (request.TurnLease is not { } turnLease)
                {
                    _logger.LogWarning(
                        "Context compression has no chat client for conversation {ConversationId} turn {TurnId}: " +
                        "no compression model was usable and the turn carries no lease.",
                        request.ConversationId,
                        request.TurnId);
                    return null;
                }

                client = turnLease.Client;
                producedProfile = turnLease.Profile;
            }

            // Streaming is the path every provider serves correctly: some OpenAI-compatible endpoints answer a
            // non-streaming request with an empty choices array, which the SDK dereferences unguarded (2026-10-18 log).
            var response = await client
                .GetStreamingResponseAsync(messages, options, cancellationToken)
                .ToChatResponseAsync(cancellationToken)
                .ConfigureAwait(false);
            var summary = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                _logger.LogWarning(
                    "Context compression returned no summary for conversation {ConversationId} turn {TurnId}.",
                    request.ConversationId,
                    request.TurnId);
                return null;
            }

            return new DirectCompressionResult(
                summary,
                producedProfile.Model,
                // One unit is one stored conversation message, so a unit carrying a tool call plus its
                // result must not inflate the message count the transcript reports.
                request.PrefixUnits.Count,
                request.SourceTokens,
                // The summary is counted with the profile that actually produced it: the dedicated
                // compression model, or the turn model when the policy's model was unusable.
                SummaryTokens: _tokenCounter.CountText(summary, producedProfile),
                usedFallbackModel);
        }
        catch (OperationCanceledException)
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

    private static List<ChatMessage> CreateSummarizerMessages(DirectCompressionRequest request)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, CompressionInstruction.Text) };
        if (!string.IsNullOrWhiteSpace(request.PreviousSummary))
        {
            messages.Add(new ChatMessage(
                ChatRole.User,
                $"<selfclaw-context-summary version=\"1\">\n{request.PreviousSummary}\n</selfclaw-context-summary>"));
        }

        // The units already pair tool calls with their results, so replaying their messages keeps the
        // roles and the tool semantics instead of flattening everything into text.
        foreach (var unit in request.PrefixUnits)
        {
            messages.AddRange(PlaceholderizeAttachments(unit.Messages));
        }

        return messages;
    }

    /// <summary>
    /// The summarizer needs the meaning of an image, not its pixels: sending the bytes would blow its own
    /// window and leak binary content into a summary. The planner already declines image-bearing units, so
    /// this is the second line of defence, and it reaches the images nested inside a native tool result.
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

    private static int OutputTokenBudget(int sourceTokens)
        => Math.Clamp(sourceTokens / 4, MinimumSummaryTokens, MaximumSummaryTokens);

    private async Task<AiChatClientLease?> TryCreateDedicatedLeaseAsync(
        Guid modelProfileId,
        CancellationToken cancellationToken)
    {
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
        catch (OperationCanceledException)
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
