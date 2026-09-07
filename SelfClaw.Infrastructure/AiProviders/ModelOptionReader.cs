using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Reads the profile's free-form <c>ModelOptions</c> JSON with a uniform "warn and skip" policy: a key
/// that is present but has the wrong JSON kind is logged and ignored, an absent key is skipped silently,
/// and <see cref="LogUnknown"/> reports keys the format does not recognise. Bound once to a profile's
/// options and name so adapters don't thread them through every call.
/// </summary>
internal sealed class ModelOptionReader
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
