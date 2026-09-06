using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Shared building blocks for provider adapters: the format-independent <see cref="ChatOptions"/> base
/// (sampling + tool wiring) that every adapter needs identically, and a typed reader for the profile's
/// free-form <c>ModelOptions</c> JSON that logs and skips mistyped or unknown keys uniformly.
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
        var sampling = request.Profile.Sampling;
        return new ChatOptions
        {
            Temperature = sampling.TemperatureEnabled ? (float)sampling.Temperature : null,
            TopP = sampling.TopPEnabled ? (float)sampling.TopP : null,
            ToolMode = request.Tools.Count > 0 ? ChatToolMode.Auto : ChatToolMode.None,
            Tools = request.Tools.Count > 0 ? request.Tools.ToList() : null,
            RawRepresentationFactory = rawRepresentationFactory
        };
    }
}

/// <summary>
/// Reads the profile's free-form <c>ModelOptions</c> JSON with a uniform "warn and skip" policy: a key
/// that is present but has the wrong JSON kind is logged and ignored, an absent key is skipped silently,
/// and <see cref="LogUnknown"/> reports keys the format does not recognise. Bound once to a profile's
/// options and name so adapters don't thread them through every call.
/// </summary>
internal readonly struct ModelOptionReader
{
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, JsonElement> _options;
    private readonly string _profileName;

    public ModelOptionReader(
        ILogger logger,
        IReadOnlyDictionary<string, JsonElement> options,
        string profileName)
    {
        _logger = logger;
        _options = options;
        _profileName = profileName;
    }

    /// <summary>Binds a reader to a model profile's options and name.</summary>
    public static ModelOptionReader ForProfile(ILogger logger, AiModelProfile profile) =>
        new(logger, profile.ModelOptions, profile.Name);

    /// <summary>Reads a string option; false (with a warning) when present but not a string, false silently when absent.</summary>
    public bool TryReadString(string key, out string value)
    {
        value = string.Empty;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            LogWrongKind(key, "a string", element.ValueKind);
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>Reads a boolean option; false (with a warning) when present but not a boolean, false silently when absent.</summary>
    public bool TryReadBool(string key, out bool value)
    {
        value = false;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            LogWrongKind(key, "a boolean", element.ValueKind);
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    /// <summary>Reads a 32-bit integer option; false (with a warning) when present but not an integer, false silently when absent.</summary>
    public bool TryReadInt(string key, out int value)
    {
        value = 0;
        if (!_options.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            LogWrongKind(key, "an integer", element.ValueKind);
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>Emits a debug log for every option key the format does not recognise.</summary>
    public void LogUnknown(IReadOnlySet<string> recognizedKeys)
    {
        foreach (var key in _options.Keys)
        {
            if (!recognizedKeys.Contains(key))
            {
                _logger.LogDebug(
                    "Ignoring unknown model option '{Key}' for profile '{Profile}'.",
                    key,
                    _profileName);
            }
        }
    }

    private void LogWrongKind(string key, string expected, JsonValueKind actual)
        => _logger.LogWarning(
            "Ignoring model option '{Key}' for profile '{Profile}': expected {Expected} but got {Kind}.",
            key,
            _profileName,
            expected,
            actual);
}
