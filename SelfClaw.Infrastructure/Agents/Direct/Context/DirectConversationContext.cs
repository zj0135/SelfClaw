using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

// Owns only this turn's native request history and client-call identities. Display projections
// never flow back into this history, so signatures and provider-specific content survive replay.
//
// The history keeps the three zones of design §4: one system text, the ordered units the turn grew by, and
// the protected messages in-turn compaction may never cover. Messages is the flattened view the provider
// receives; it is rebuilt whenever the structure changes so no caller can observe a half-applied edit.
internal sealed class DirectConversationContext
{
    // One entry is either a unit (compressible, ordered by SourceSequence) or a protected message.
    // Both live in one list so the composed order is exactly the order they were added in.
    private sealed record ContextEntry(IReadOnlyList<ChatMessage> Messages, long SourceSequence, bool IsProtected);

    private readonly string? _systemText;
    private readonly List<ContextEntry> _entries;
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private long _nextUnitSequence;
    private string? _inTurnSummary;
    private string? _planSection;
    private IReadOnlyList<ChatMessage> _messages = [];
    private int _countedMessageCount;
    private int _countedTokens;

    internal DirectConversationContext(
        string? systemText,
        IReadOnlyList<DirectPromptHistoryUnit> initialUnits,
        IReadOnlyList<ChatMessage> trailingProtectedMessages)
    {
        ArgumentNullException.ThrowIfNull(initialUnits);
        ArgumentNullException.ThrowIfNull(trailingProtectedMessages);
        _systemText = systemText;
        _entries = new List<ContextEntry>(initialUnits.Count + trailingProtectedMessages.Count);
        foreach (var unit in initialUnits)
        {
            _entries.Add(new ContextEntry(unit.Messages, unit.SourceSequence, IsProtected: false));
        }

        foreach (var message in trailingProtectedMessages)
        {
            _entries.Add(new ContextEntry([message], 0, IsProtected: true));
        }

        _nextUnitSequence = NextUnitSequence(initialUnits);
        Recompose();
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>
    /// The summary text this turn already produced, or null before its first in-turn compaction. It is the
    /// bare text, not the wrapped section: a chained compaction hands it back to the compressor as the
    /// previous summary, which wraps it itself, exactly like the turn-start compaction record does.
    /// </summary>
    public string? InTurnSummary => _inTurnSummary;

    /// <summary>
    /// Counts the history once and then only what was appended since. The loop asks whether the next
    /// request still fits on every provider round-trip, and re-tokenizing the whole transcript each time
    /// would make a long turn quadratic. Returns null when counting is unavailable for the model (§5.5).
    /// </summary>
    public int? CountTokens(IDirectTokenCounter tokenCounter, AiModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        var total = _countedTokens;
        for (var index = _countedMessageCount; index < _messages.Count; index++)
        {
            if (tokenCounter.CountMessages([_messages[index]], profile) is not int tokens)
            {
                // The tally is not advanced, so a later request retries the same tail instead of double counting.
                return null;
            }

            total += tokens;
        }

        _countedTokens = total;
        _countedMessageCount = _messages.Count;
        return total;
    }

    /// <summary>
    /// The compressible units this turn currently holds, in order. Protected messages are never units.
    /// </summary>
    public IReadOnlyList<DirectPromptHistoryUnit> BuildUnits()
        => _entries.Where(entry => !entry.IsProtected)
            .Select(entry => new DirectPromptHistoryUnit(entry.Messages, entry.SourceSequence,
                ContainsAttachments: entry.Messages.Any(ContainsAttachment)))
            .ToArray();

    public FunctionCallContent[] AppendResponse(ChatResponse response,
        IReadOnlyDictionary<string, DirectToolBinding> bindings)
    {
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.CallId) || !_callIds.Add(call.CallId))
                throw new InvalidDataException($"Invalid or repeated Direct tool call id '{call.CallId}'.");
            if (call.Exception is not null)
                throw new InvalidDataException($"Invalid arguments for Direct tool '{call.Name}'.", call.Exception);
            if (!bindings.ContainsKey(call.Name))
                throw new InvalidDataException($"Direct tool '{call.Name}' is not bound to this turn.");
        }
        if (calls.Length == 0 && response.FinishReason == ChatFinishReason.ToolCalls)
            throw new InvalidDataException("The provider ended with tool_calls but supplied no complete client tool call.");

        // One response opens one unit: compaction boundaries may only fall between round trips.
        _entries.Add(new ContextEntry([.. response.Messages], _nextUnitSequence++, IsProtected: false));
        Recompose();
        return calls;
    }

    public void AppendResults(IList<AIContent> results, IReadOnlyList<ChatMessage>? followUps = null)
    {
        // A tool result joins the response it answers instead of opening its own unit, so a call and its
        // result can never be separated by an in-turn compaction.
        var index = _entries.FindLastIndex(entry => !entry.IsProtected);
        if (index < 0)
            throw new InvalidDataException("Tool results require a preceding assistant response in this turn.");

        // The messages carrying a batch's images belong to the same unit for the same reason: a boundary
        // between a tool result and its images would leave the model with images it cannot attribute.
        var messages = new List<ChatMessage>(1 + (followUps?.Count ?? 0)) { new(ChatRole.Tool, results) };
        if (followUps is not null)
        {
            messages.AddRange(followUps);
        }

        var entry = _entries[index];
        _entries[index] = entry with { Messages = [.. entry.Messages, .. messages] };
        Recompose();
    }

    public void AppendInputs(IReadOnlyList<MessageRecord> messages,
        IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>? attachmentContents = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages.OrderBy(message => message.Sequence))
        {
            if (message.Role != MessageRole.User)
                throw new InvalidDataException("A consumed input must be a user message.");
            // A consumed steer is protected input for the rest of the turn (direct-message-input-design):
            // the model must still see it after any later compaction, and only the next turn's turn-start
            // compaction may fold it into a summary.
            _entries.Add(new ContextEntry([BuildInputMessage(message, attachmentContents?.GetValueOrDefault(message.Id))],
                0, IsProtected: true));
        }

        Recompose();
    }

    /// <summary>
    /// Drops the oldest <paramref name="coveredUnitCount"/> units and installs <paramref name="summary"/> in
    /// their place. Protected messages keep their position, and a second compaction in the same turn replaces
    /// the summary instead of stacking another one on top of it.
    /// </summary>
    public void ApplyInTurnCompaction(string summary, int coveredUnitCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var units = _entries.Count(entry => !entry.IsProtected);
        if (coveredUnitCount < 0 || coveredUnitCount > units)
        {
            throw new ArgumentOutOfRangeException(nameof(coveredUnitCount), coveredUnitCount,
                $"The turn holds {units} units, so {coveredUnitCount} of them cannot be compacted.");
        }

        // A plan written into the units this summary now covers would otherwise vanish from the model's view, and
        // it is exactly the oldest units that in-turn compaction takes first. It has to be read before the units
        // go, and assigned before Recompose, which is what puts the section into the system text.
        if (FindCoveredPlan(coveredUnitCount) is { } coveredPlan &&
            TodoPlanSection.Build(coveredPlan) is { } section &&
            !string.Equals(section, _planSection, StringComparison.Ordinal))
        {
            _planSection = section;
        }

        var removed = 0;
        for (var index = 0; index < _entries.Count && removed < coveredUnitCount;)
        {
            if (_entries[index].IsProtected)
            {
                index++;
                continue;
            }

            _entries.RemoveAt(index);
            removed++;
        }

        _inTurnSummary = summary;
        // The composed list no longer matches the tally, so the next CountTokens rebuilds it from scratch.
        _countedMessageCount = 0;
        _countedTokens = 0;
        Recompose();
    }

    // Only the newest plan of the turn decides: a newer todo_write in a retained unit is still in the history,
    // so an older covered snapshot must not resurface behind it.
    private TodoPlan? FindCoveredPlan(int coveredUnitCount)
    {
        var unitIndex = -1;
        var newestIndex = -1;
        TodoPlan? newest = null;
        foreach (var entry in _entries)
        {
            if (entry.IsProtected)
            {
                continue;
            }

            unitIndex++;
            if (TodoPlanSection.FindNewestPlan(entry.Messages) is { } plan)
            {
                newestIndex = unitIndex;
                newest = plan;
            }
        }

        return newestIndex >= 0 && newestIndex < coveredUnitCount ? newest : null;
    }

    private void Recompose()
    {
        var composed = new List<ChatMessage>();
        if (ComposeSystemText() is { } systemText)
        {
            composed.Add(new ChatMessage(ChatRole.System, systemText));
        }

        foreach (var entry in _entries)
        {
            composed.AddRange(entry.Messages);
        }

        _messages = composed;
    }

    // The in-turn summary is wrapped into its section here, so the format stays owned by the composer, and it
    // is appended at the end of the prompt's own system text: the sections the turn started with keep their
    // exact byte layout, and this path only exists once in-turn compaction ran.
    private string? ComposeSystemText()
    {
        // A turn-start snapshot may already sit in the system text this turn began with; the same list must not
        // be paid for twice when an in-turn compaction covers it again (design §7.4).
        var planSection = _planSection is not null &&
                          _systemText?.Contains(_planSection, StringComparison.Ordinal) == true
            ? null
            : _planSection;
        var sections = new[]
            {
                _systemText,
                _inTurnSummary is null ? null : DirectPromptComposer.CreateSummarySection(_inTurnSummary),
                planSection
            }
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToArray();
        return sections.Length == 0 ? null : string.Join("\n\n", sections);
    }

    // In-turn units are identified by a synthetic sequence because the recorder owns the stored message
    // sequences. Compaction only ever considers units with a positive one, so the counter starts above the
    // replayed history and above zero even for an empty history.
    private static long NextUnitSequence(IReadOnlyList<DirectPromptHistoryUnit> initialUnits)
    {
        long maximum = 0;
        foreach (var unit in initialUnits)
        {
            maximum = Math.Max(maximum, unit.SourceSequence);
        }

        return maximum + 1;
    }

    // A steer carrying images is replayed exactly like a replayed history message: attachments first, text last.
    private static ChatMessage BuildInputMessage(MessageRecord message, IReadOnlyList<AIContent>? images)
    {
        if (images is not { Count: > 0 })
        {
            return new ChatMessage(ChatRole.User, message.MarkdownContent);
        }

        var contents = new List<AIContent>(images.Count + 1);
        contents.AddRange(images);
        if (!string.IsNullOrEmpty(message.MarkdownContent))
        {
            contents.Add(new TextContent(message.MarkdownContent));
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    // A tool result on the native delivery path nests its image inside the result, so a shallow check would
    // report a unit full of images as text and let the planner summarize it away.
    private static bool ContainsAttachment(ChatMessage message)
        => message.Contents.Any(ContainsAttachment);

    private static bool ContainsAttachment(AIContent content) => content switch
    {
        DataContent => true,
        FunctionResultContent result => ResultContainsAttachment(result.Result),
        _ => false
    };

    private static bool ResultContainsAttachment(object? value) => value switch
    {
        AIContent content => ContainsAttachment(content),
        IEnumerable<AIContent> contents => contents.Any(ContainsAttachment),
        _ => false
    };
}
