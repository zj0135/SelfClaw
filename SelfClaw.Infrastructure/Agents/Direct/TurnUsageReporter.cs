using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.AiProviders;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// One Direct turn's metering: it accumulates what the provider reported, prices the aggregate with the
/// model's configuration and reports it once. It owns only "what did this turn cost", so stream translation
/// and turn control flow stay free of usage arithmetic.
/// </summary>
internal sealed class TurnUsageReporter
{
    private static readonly string[] CacheWriteTokenKeys =
    [
        "CacheCreationInputTokens",
        "cache_creation_input_tokens",
        "cacheCreationInputTokens"
    ];

    private readonly TurnUsageAccumulator _accumulator = new();
    private AiModelConfiguration? _configuration;
    private string? _model;
    private int? _contextWindowTokens;
    private bool _reported;

    /// <summary>Supplies the pricing, model id and context window the accumulated usage is reported with.</summary>
    public void Configure(AiModelConfiguration? configuration, string? model, int? contextWindowTokens)
    {
        _configuration = configuration;
        _model = model;
        _contextWindowTokens = contextWindowTokens;
    }

    /// <summary>
    /// Records one provider call; every observation counts as one request. The input count travels as
    /// <see cref="TurnUsage.LastRequestInputTokens"/> because it is the real size of the replayed context;
    /// the accumulator takes the latest observation and never sums it.
    /// </summary>
    public void Observe(UsageDetails details)
    {
        var inputTokens = ClampTokensOrNull(details.InputTokenCount);
        var outputTokens = ClampTokensOrNull(details.OutputTokenCount);
        _accumulator.Observe(new TurnUsage(
            InputTokens: inputTokens,
            CachedInputTokens: ClampTokensOrNull(details.CachedInputTokenCount),
            CacheWriteInputTokens: ReadCacheWriteTokens(details),
            OutputTokens: outputTokens,
            ReasoningTokens: ClampTokensOrNull(details.ReasoningTokenCount),
            TotalTokens: ClampTokensOrNull(details.TotalTokenCount),
            ProviderCalls: 1,
            ContextTokens: inputTokens is int input && outputTokens is int output ? input + output : null,
            LastRequestInputTokens: inputTokens,
            AdditionalCountsJson: details.AdditionalCounts is { Count: > 0 } counts
                ? JsonSerializer.Serialize(counts)
                : null));
    }

    /// <summary>The turn's aggregate so far, priced when the model configuration allows an estimate.</summary>
    public TurnUsage? Build()
    {
        if (_accumulator.Build() is not { } usage)
        {
            return null;
        }

        var cost = TurnUsageCostCalculator.ComputeCostUsdMicros(usage, _configuration);
        return usage with
        {
            Model = _model ?? usage.Model,
            ContextWindowTokens = _contextWindowTokens ?? usage.ContextWindowTokens,
            CostUsdMicros = cost,
            CostSource = cost is null ? TurnUsageCostSource.None : TurnUsageCostSource.Estimated
        };
    }

    /// <summary>Reports the turn's usage once; later calls are no-ops.</summary>
    public void Report(ChannelWriter<AgentStreamEvent> writer)
    {
        if (_reported)
        {
            return;
        }

        _reported = true;
        if (Build() is { } usage)
        {
            writer.TryWrite(new UsageReportedEvent(usage));
        }
    }

    /// <summary>
    /// Publishes the turn's usage so far for live display. This is a snapshot, not an observation: it never
    /// enters the accumulator, so the terminal report still carries the turn's real totals. A turn that has
    /// already reported stays silent, which keeps the terminal usage the last usage event of the run.
    /// </summary>
    public void PublishProgress(ChannelWriter<AgentStreamEvent> writer)
    {
        if (_reported)
        {
            return;
        }

        if (Build() is { } usage)
        {
            writer.TryWrite(new UsageProgressEvent(usage));
        }
    }

    /// <summary>
    /// The Anthropic SDK reports cache-write tokens through <see cref="UsageDetails.AdditionalCounts"/>
    /// instead of a first-class property; accept the snake-case spelling as well so a future SDK
    /// revision cannot silently drop them.
    /// </summary>
    private static int? ReadCacheWriteTokens(UsageDetails details)
    {
        if (details.AdditionalCounts is not { Count: > 0 } counts)
        {
            return null;
        }

        foreach (var key in CacheWriteTokenKeys)
        {
            if (counts.TryGetValue(key, out var value))
            {
                return ClampTokensOrNull(value);
            }
        }

        return null;
    }

    private static int? ClampTokensOrNull(long? tokens)
        => tokens is long value ? (int)Math.Clamp(value, 0, int.MaxValue) : null;
}
