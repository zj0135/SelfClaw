using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

// All currently shipped adapters expose request totals (Anthropic already merges its wire deltas).
// Incremental adapters must explicitly declare that contract on their lease.
internal sealed class RequestUsageAccumulator(AiUsageUpdateKind kind)
{
    private UsageDetails? _usage;
    private bool _incompleteIncrementalTotal;

    public UsageDetails? Build()
    {
        if (_usage is null) return null;
        var normalized = new UsageDetails();
        normalized.Add(_usage);
        if (kind == AiUsageUpdateKind.Incremental)
        {
            if (_incompleteIncrementalTotal) normalized.TotalTokenCount = null;
            return normalized;
        }
        // Derive before turn accumulation: mixing reported and derived request totals must
        // not cause the turn accumulator to retain only the explicitly reported subset.
        normalized.TotalTokenCount ??= normalized.InputTokenCount is long input && normalized.OutputTokenCount is long output
            ? input + output
            : null;
        return normalized;
    }

    public void Observe(UsageDetails update)
    {
        _usage ??= new UsageDetails();
        if (kind == AiUsageUpdateKind.Incremental)
        {
            // Each delta owns its optional counters. Deriving from the whole response would
            // discard total-only deltas and combine unrelated, incomplete component deltas.
            var fragment = new UsageDetails();
            fragment.Add(update);
            fragment.TotalTokenCount ??= update.InputTokenCount is long input && update.OutputTokenCount is long output
                ? input + output
                : null;
            _incompleteIncrementalTotal |= fragment.TotalTokenCount is null &&
                (update.InputTokenCount is not null || update.OutputTokenCount is not null);
            _usage.Add(fragment);
            return;
        }

        _usage.InputTokenCount = update.InputTokenCount ?? _usage.InputTokenCount;
        _usage.OutputTokenCount = update.OutputTokenCount ?? _usage.OutputTokenCount;
        _usage.TotalTokenCount = update.TotalTokenCount ??
            (update.InputTokenCount is not null || update.OutputTokenCount is not null ? null : _usage.TotalTokenCount);
        _usage.CachedInputTokenCount = update.CachedInputTokenCount ?? _usage.CachedInputTokenCount;
        _usage.ReasoningTokenCount = update.ReasoningTokenCount ?? _usage.ReasoningTokenCount;
#pragma warning disable MEAI001 // Preserve the SDK's modality counts while normalizing snapshots.
        _usage.InputAudioTokenCount = update.InputAudioTokenCount ?? _usage.InputAudioTokenCount;
        _usage.InputTextTokenCount = update.InputTextTokenCount ?? _usage.InputTextTokenCount;
        _usage.OutputAudioTokenCount = update.OutputAudioTokenCount ?? _usage.OutputAudioTokenCount;
        _usage.OutputTextTokenCount = update.OutputTextTokenCount ?? _usage.OutputTextTokenCount;
#pragma warning restore MEAI001
        if (update.AdditionalCounts is { } counts)
        {
            _usage.AdditionalCounts ??= new();
            foreach (var (key, value) in counts) _usage.AdditionalCounts[key] = value;
        }
    }
}
