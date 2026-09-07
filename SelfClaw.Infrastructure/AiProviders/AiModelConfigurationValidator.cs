using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

internal static class AiModelConfigurationValidator
{
    public static AiModelConfiguration Validate(AiModelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Name);
        ArgumentNullException.ThrowIfNull(configuration.Sampling);
        var sampling = configuration.Sampling;
        if (!double.IsFinite(sampling.Temperature) || sampling.Temperature is < 0 or > 2)
        {
            throw new ArgumentException("Temperature must be between 0 and 2.");
        }

        if (!double.IsFinite(sampling.TopP) || sampling.TopP is < 0 or > 1)
        {
            throw new ArgumentException("Top P must be between 0 and 1.");
        }

        if (configuration.ReasoningEffort is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
        {
            throw new ArgumentException("Unsupported reasoning effort.");
        }

        ValidateLimits(configuration);
        if (configuration.PriceInPerMTok < 0 || configuration.PriceOutPerMTok < 0 ||
            configuration.PriceCacheReadPerMTok < 0 || configuration.PriceCacheWritePerMTok < 0)
        {
            throw new ArgumentException("Token prices must be non-negative USD amounts per million tokens.");
        }

        if (configuration.Model.Length > 256 || configuration.Name.Length > 256 || configuration.Description?.Length > 4000)
        {
            throw new ArgumentException("Model ID and name must not exceed 256 characters; description must not exceed 4000.");
        }

        return configuration with
        {
            Model = configuration.Model.Trim(),
            Name = configuration.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(configuration.Description) ? null : configuration.Description.Trim()
        };
    }

    private static void ValidateLimits(AiModelConfiguration configuration)
    {
        if (configuration.ContextLength <= 0 || configuration.MaxOutputTokens <= 0)
        {
            throw new ArgumentException("Token limits must be positive integers or left unset.");
        }

        if (configuration.ContextLength is int context && configuration.MaxOutputTokens is int output && output >= context)
        {
            throw new ArgumentException("Maximum output tokens must be smaller than the context length.");
        }
    }
}
