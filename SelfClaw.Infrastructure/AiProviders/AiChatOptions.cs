using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Shared sampling, output limits and tool wiring for provider adapters.
/// </summary>
internal static class AiChatOptions
{
    /// <summary>
    /// Model option every format understands for capping a turn's output length. Each adapter
    /// maps it onto <see cref="ChatOptions.MaxOutputTokens"/> so the ceiling is configured the
    /// same way regardless of provider.
    /// </summary>
    public const string MaxOutputTokensKey = "max_output_tokens";

    /// <summary>
    /// Model option declaring the model's context window. It drives the Direct prompt history budget:
    /// history is trimmed to leave room for the system prompt and the output reserve. When it is
    /// unset, the full history is sent.
    /// </summary>
    public const string ContextWindowTokensKey = "context_window_tokens";

    /// <summary>
    /// Display metadata written by model-list refresh from the provider's own catalog. It holds
    /// the model's true output ceiling, which is a far better default than whatever the provider
    /// SDK falls back to.
    /// </summary>
    private const string CatalogMaxOutputTokensKey = "display.maxOutputTokens";

    /// <summary>
    /// Reads the profile's declared context window; null when unset. A mistyped value is treated as
    /// unset here because this reader runs before logging is bound to the turn's adapter.
    /// </summary>
    public static int? ResolveContextWindowTokens(AiModelProfile profile)
    {
        if (profile.Configuration?.ContextLength is int configured)
        {
            return configured;
        }

        if (profile.ModelOptions.TryGetValue(ContextWindowTokensKey, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var tokens) &&
            tokens > 0)
        {
            return tokens;
        }

        return null;
    }

    /// <summary>
    /// Resolves the output-token ceiling for a turn: an explicitly configured value wins, otherwise
    /// the model's catalog-reported maximum is used. Leaving this null hands the decision to the
    /// provider, and some providers default low enough to cut ordinary answers off mid-sentence
    /// (the Anthropic integration sends 4096), which surfaces as a response that stops for no
    /// visible reason.
    /// </summary>
    public static int? ResolveMaxOutputTokens(AiProviderClientRequest request, int? configured)
    {
        if (request.Profile.Configuration?.MaxOutputTokens is int sharedMaximum)
        {
            return sharedMaximum;
        }

        if (configured > 0)
        {
            return configured;
        }

        return request.Profile.ModelOptions.TryGetValue(CatalogMaxOutputTokensKey, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out var catalogMaximum) &&
               catalogMaximum > 0
            ? catalogMaximum
            : null;
    }

    /// <summary>
    /// Builds the format-independent <see cref="ChatOptions"/> shared by every adapter: sampling pulled
    /// from the profile, and tool mode/list derived from the request. <paramref name="rawRepresentationFactory"/>
    /// wires a provider-specific raw options object when the adapter needs one.
    /// </summary>
    public static ChatOptions CreateBase(
        AiProviderClientRequest request,
        Func<IChatClient, object?>? rawRepresentationFactory = null)
    {
        var sampling = request.Profile.Configuration?.Sampling ?? request.Profile.Sampling;
        return new ChatOptions
        {
            Temperature = sampling.TemperatureEnabled ? (float)sampling.Temperature : null,
            TopP = sampling.TopPEnabled ? (float)sampling.TopP : null,
            MaxOutputTokens = ResolveMaxOutputTokens(request,
                request.Profile.ModelOptions.TryGetValue(MaxOutputTokensKey, out var maximum) &&
                maximum.ValueKind == JsonValueKind.Number &&
                maximum.TryGetInt32(out var tokens) ? tokens : null),
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = request.Tools.Count > 0 ? request.Tools.ToList() : null,
            RawRepresentationFactory = rawRepresentationFactory
        };
    }
}
