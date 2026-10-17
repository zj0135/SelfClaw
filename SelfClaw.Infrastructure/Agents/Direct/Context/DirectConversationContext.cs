using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

// Owns only this turn's native request history and client-call identities. Display projections
// never flow back into this history, so signatures and provider-specific content survive replay.
internal sealed class DirectConversationContext(IReadOnlyList<ChatMessage> messages)
{
    private readonly List<ChatMessage> _messages = [.. messages];
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private int _countedMessageCount;
    private int _countedTokens;

    public IReadOnlyList<ChatMessage> Messages => _messages;

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

        _messages.AddRange(response.Messages);
        return calls;
    }

    public void AppendResults(IList<AIContent> results) => _messages.Add(new ChatMessage(ChatRole.Tool, results));

    public void AppendInputs(IReadOnlyList<SelfClaw.Core.Models.MessageRecord> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages.OrderBy(message => message.Sequence))
        {
            if (message.Role != SelfClaw.Core.Models.MessageRole.User)
                throw new InvalidDataException("A consumed input must be a user message.");
            _messages.Add(new ChatMessage(ChatRole.User, message.MarkdownContent));
        }
    }
}
