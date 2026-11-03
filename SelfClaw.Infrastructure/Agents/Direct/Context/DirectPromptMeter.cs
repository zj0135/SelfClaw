using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The one place a Direct prompt is counted: the system sections, the tool definitions and the units that
/// actually get replayed. Every entry returns null instead of an estimate when the counter cannot size the
/// model, so the fail-open degradation (design §5.5) is decided here once instead of at every call site.
/// </summary>
internal sealed class DirectPromptMeter
{
    private readonly IDirectTokenCounter _tokenCounter;

    internal DirectPromptMeter(IDirectTokenCounter tokenCounter)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        _tokenCounter = tokenCounter;
    }

    /// <summary>
    /// Counts the prompt the turn-start path is about to send: the system text (including an existing summary),
    /// the tool definitions and every unit that still gets replayed. Units an existing summary already covers
    /// are not counted because they never reach the request.
    /// </summary>
    internal DirectCountedPrompt? CountTurnStartPrompt(
        string? systemText,
        IEnumerable<AITool>? tools,
        AiModelProfile profile,
        IReadOnlyList<DirectPromptHistoryUnit> units,
        long coveredThroughSequence,
        int outputReserve)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (_tokenCounter.CountText(systemText, profile) is not int systemTokens)
        {
            return null;
        }

        if (_tokenCounter.CountTools(tools, profile) is not int toolTokens)
        {
            return null;
        }

        var total = systemTokens + toolTokens;
        var counted = new List<DirectPromptHistoryUnit>(units.Count);
        foreach (var unit in units)
        {
            if (unit.SourceSequence <= coveredThroughSequence)
            {
                counted.Add(unit);
                continue;
            }

            if (_tokenCounter.CountMessages(unit.Messages, profile) is not int unitTokens)
            {
                return null;
            }

            counted.Add(unit with { Tokens = unitTokens });
            total += unitTokens;
        }

        return new DirectCountedPrompt(total, total + outputReserve, counted);
    }

    /// <summary>
    /// Counts the request the in-turn decisions make: the context the turn grew plus the tool definitions. The
    /// context counts incrementally, so a round trip extends the tally instead of re-tokenizing the turn.
    /// </summary>
    internal DirectPromptMeasurement? CountContextPrompt(
        DirectConversationContext context,
        IEnumerable<AITool>? tools,
        AiModelProfile profile,
        int outputReserve)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CountTokens(_tokenCounter, profile) is not int contextTokens)
        {
            return null;
        }

        if (_tokenCounter.CountTools(tools, profile) is not int toolTokens)
        {
            return null;
        }

        var total = contextTokens + toolTokens;
        return new DirectPromptMeasurement(total, total + outputReserve);
    }

    /// <summary>
    /// Counts every unit so the planner can size a prefix. One unit without a count makes the whole prompt
    /// unsizable: a boundary cannot be planned across a unit nobody measured.
    /// </summary>
    internal IReadOnlyList<DirectPromptHistoryUnit>? CountUnits(
        IReadOnlyList<DirectPromptHistoryUnit> units,
        AiModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(units);
        var counted = new List<DirectPromptHistoryUnit>(units.Count);
        foreach (var unit in units)
        {
            if (_tokenCounter.CountMessages(unit.Messages, profile) is not int tokens)
            {
                return null;
            }

            counted.Add(unit with { Tokens = tokens });
        }

        return counted;
    }

    /// <summary>
    /// The size of one pending input, counted before it joins the context so a steer batch is admitted or held
    /// without being appended first. Null means counting is unavailable.
    /// </summary>
    internal int? CountInputText(string? prompt, AiModelProfile profile)
        => _tokenCounter.CountText(prompt, profile);
}
