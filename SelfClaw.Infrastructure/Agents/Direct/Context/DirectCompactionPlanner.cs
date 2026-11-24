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
    /// The share of the trigger a compacted prompt is brought back to. It is a constant rather than a setting
    /// so the retention behaviour cannot drift below "always send the newest turns", and it sizes the whole
    /// prompt rather than the history alone: whatever a summary cannot replace (instructions, tool
    /// definitions, protected messages) comes out of the retained history, or a compacted prompt could still
    /// sit at the trigger and be compacted again on every round trip.
    /// </summary>
    internal const double RetainedContextRatio = 0.5d;

    /// <summary>
    /// How much of the usable input space (the window minus the output reserve) the trigger may reach. The
    /// local count runs a few percent under a provider's own (a foreign tokenizer, wire framing), so a trigger
    /// at the hard limit would wave through a request the provider then rejects.
    /// </summary>
    internal const double UsableInputShare = 0.9d;

    /// <summary>
    /// Plans compaction from already counted units. Every decline returns a plan with no prefix and the
    /// trigger that was decided against, so the caller can still report what it measured.
    /// </summary>
    /// <param name="observedTokens">
    /// The provider's own measurement of this context, when the caller holds one that describes the prompt
    /// being planned. It can only start compaction earlier: the local tokenizer approximates a provider's and
    /// sizes an image by a constant, so a measurement at the trigger starts compaction even while the local
    /// count is still below it. Which units the summary replaces is decided by the unit counts alone.
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
        var trigger = ResolveTrigger(policy, budget);

        if (!policy.Enabled || countedTokens is not int counted ||
            Math.Max(counted, observedTokens ?? 0) < trigger ||
            candidates.Length == 0)
        {
            return Decline(trigger);
        }

        // A unit the caller never counted cannot be sized, so compaction degrades to "not this turn"
        // instead of estimating (design §5.5). This is the only null check: retention stops as soon as the
        // target is reached, so the older prefix units are never visited by the retention walk below and
        // have to be validated here with the rest.
        var unitTokens = new int[candidates.Length];
        var candidateTokens = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            if (candidates[index].Tokens is not int tokens)
            {
                return Decline(trigger);
            }

            unitTokens[index] = tokens;
            candidateTokens += tokens;
        }

        // Whatever the count holds beyond the candidates is what no summary can replace, and it stays in the
        // compacted prompt. A measurement can start compaction while the count is still small, so the
        // difference is floored instead of trusted to be positive.
        var fixedTokens = Math.Max(0, counted - candidateTokens);
        var keepTarget = Math.Max(0, (int)(trigger * RetainedContextRatio) - fixedTokens);
        var prefixLength = candidates.Length - CountRetainedUnits(unitTokens, keepTarget);
        if (prefixLength <= 0)
        {
            return Decline(trigger);
        }

        var prefix = candidates.Take(prefixLength).ToArray();
        var sourceTokens = 0;
        for (var index = 0; index < prefixLength; index++)
        {
            sourceTokens += unitTokens[index];
        }

        return new DirectCompressionPlan(true, prefix, prefix[^1].SourceSequence, sourceTokens, trigger);
    }

    /// <summary>
    /// The single trigger formula, as a prompt size: the configured share of the declared window, capped by
    /// what the output reserve leaves usable, or the absolute threshold when the model declares no window.
    /// The reserve caps the trigger instead of being added to the count, so the number compared here is the
    /// one the context readout shows: a model whose output cap is a large part of its window would otherwise
    /// trigger far below the configured share, and below what a compaction retains. The turn-start planner
    /// and the in-turn checkpoint both call this, so the two can never drift apart into different notions of
    /// "the context is getting large".
    /// </summary>
    internal static int ResolveTrigger(DirectCompressionPolicy policy, DirectPromptBudget budget)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (budget.ContextWindowTokens is not int window)
        {
            return policy.AbsoluteThresholdTokens;
        }

        var usableInput = window - ResolveOutputReserve(budget.MaxOutputTokens);
        return Math.Max(0, Math.Min((int)(window * policy.ThresholdRatio), (int)(usableInput * UsableInputShare)));
    }

    /// <summary>
    /// The output space a response may use, and the single definition of the default reserve: the trigger cap
    /// and every window check must hold back the same amount, or a prompt can be judged to fit and then not.
    /// </summary>
    internal static int ResolveOutputReserve(int? maxOutputTokens)
        => maxOutputTokens ?? DefaultOutputTokenReserve;

    // Walks back from the newest unit and keeps as many as fit the target.
    private static int CountRetainedUnits(int[] unitTokens, int keepTarget)
    {
        var retainedUnits = 0;
        var retainedTokens = 0;
        for (var index = unitTokens.Length - 1; index >= 0; index--)
        {
            // The newest unit is always retained, even when it alone exceeds the retention target.
            if (retainedUnits > 0 && retainedTokens + unitTokens[index] > keepTarget)
            {
                break;
            }

            retainedUnits++;
            retainedTokens += unitTokens[index];
        }

        return retainedUnits;
    }

    private static DirectCompressionPlan Decline(int triggerTokens)
        => new(false, [], 0, null, triggerTokens);
}
