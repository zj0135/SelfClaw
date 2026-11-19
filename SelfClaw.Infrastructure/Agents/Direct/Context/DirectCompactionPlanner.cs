using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The single compaction decision: whether this prompt is large enough to summarize, and which whole units the
/// summary replaces. It performs no I/O, counts nothing and throws nothing: a configuration problem (compaction
/// disabled, units the caller never counted) surfaces as "do not compact", so the caller still decides how an
/// oversized request is reported.
/// </summary>
internal static class DirectCompactionPlanner
{
    /// <summary>
    /// Output space held back when the profile declares a context window but no output cap: without a
    /// reserve, a full history can leave the model no room to answer. Matches the SDK's own low default.
    /// </summary>
    internal const int DefaultOutputTokenReserve = 4096;

    /// <summary>
    /// Compression keeps this share of the trigger threshold as recent verbatim history. It is a constant
    /// rather than a setting so the retention behaviour cannot drift below "always send the newest turns".
    /// </summary>
    internal const double RetainedContextRatio = 0.5d;

    /// <summary>
    /// Plans compaction from already counted units. Every decline returns a plan with no prefix and the
    /// threshold that was decided against, so the caller can still report what it measured.
    /// </summary>
    /// <param name="observedTokens">
    /// The provider's own measurement of this context, when the caller holds one that describes the prompt
    /// being planned. It can only raise the trigger: the local tokenizer approximates a provider's and sizes
    /// an image by a constant, so a measurement at the threshold starts compaction even while the local count
    /// is still below it. Which units the summary replaces is decided by the unit counts alone.
    /// </param>
    internal static DirectCompressionPlan PlanCompression(
        IReadOnlyList<DirectPromptHistoryUnit> units,
        ConversationCompactionRecord? existing,
        DirectCompressionPolicy policy,
        DirectPromptBudget budget,
        int? countedTokens,
        int? observedTokens = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(policy);
        var existingBoundary = existing?.ThroughSequence ?? 0;
        // Every unit after the boundary is a candidate, an image-bearing one included: the compressor hands
        // the summarizer a text placeholder for each image, so no unit is a barrier the boundary cannot cross.
        // Stopping at the first image would leave everything after it uncompactable until the window overflows.
        var candidates = units.Where(unit => unit.SourceSequence > existingBoundary).ToArray();
        var threshold = ResolveThreshold(policy, budget);

        // The reserve belongs to the trigger side: on a small window a prompt just under the threshold can
        // still overflow once the response is written, and compacting is the only way to make room. Prompt
        // counts stay reserve-free because the context-size readout reports them that way.
        if (!policy.Enabled || countedTokens is not int counted ||
            Math.Max(counted + ResolveOutputReserve(budget.MaxOutputTokens), observedTokens ?? 0) < threshold ||
            candidates.Length == 0)
        {
            return Decline(threshold);
        }

        // A unit the caller never counted cannot be sized, so compaction degrades to "not this turn"
        // instead of estimating (design §5.5). This is the only null check: retention stops as soon as the
        // target is reached, so the older prefix units are never visited by the retention walk below and
        // have to be validated here with the rest.
        var unitTokens = new int[candidates.Length];
        for (var index = 0; index < candidates.Length; index++)
        {
            if (candidates[index].Tokens is not int tokens)
            {
                return Decline(threshold);
            }

            unitTokens[index] = tokens;
        }

        var keepTarget = (int)(threshold * RetainedContextRatio);
        var retainedUnits = 0;
        var retainedTokens = 0;
        for (var index = candidates.Length - 1; index >= 0; index--)
        {
            // The newest unit is always retained, even when it alone exceeds the retention target.
            if (retainedUnits > 0 && retainedTokens + unitTokens[index] > keepTarget)
            {
                break;
            }

            retainedUnits++;
            retainedTokens += unitTokens[index];
        }

        var prefixLength = candidates.Length - retainedUnits;
        if (prefixLength <= 0)
        {
            return Decline(threshold);
        }

        var prefix = candidates.Take(prefixLength).ToArray();
        var sourceTokens = 0;
        for (var index = 0; index < prefixLength; index++)
        {
            sourceTokens += unitTokens[index];
        }

        return new DirectCompressionPlan(true, prefix, prefix[^1].SourceSequence, sourceTokens, threshold);
    }

    /// <summary>
    /// The single trigger formula: a share of the declared window, or the absolute threshold when the model
    /// declares none. The turn-start planner and the in-turn checkpoint both call this, so the two can never
    /// drift apart into different notions of "the context is getting large".
    /// </summary>
    internal static int ResolveThreshold(DirectCompressionPolicy policy, DirectPromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return budget.ContextWindowTokens is int window
            ? (int)(window * policy.ThresholdRatio)
            : policy.AbsoluteThresholdTokens;
    }

    /// <summary>
    /// The output space a response may use, and the single definition of the default reserve: the trigger side
    /// and every window check must hold back the same amount, or a prompt can be judged to fit and then not.
    /// </summary>
    internal static int ResolveOutputReserve(int? maxOutputTokens)
        => maxOutputTokens ?? DefaultOutputTokenReserve;

    private static DirectCompressionPlan Decline(int thresholdTokens)
        => new(false, [], 0, null, thresholdTokens);
}
