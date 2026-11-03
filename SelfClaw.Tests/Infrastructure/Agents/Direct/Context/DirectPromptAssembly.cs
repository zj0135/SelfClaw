using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The production assembly path of one Direct prompt, flattened for assertions: a turn builds the units, the
/// system text and the protected tail, composes them into a <see cref="DirectPromptBuild"/> and sends the
/// system message first, then every replayed unit, then the protected messages.
/// </summary>
internal static class DirectPromptAssembly
{
    internal static IReadOnlyList<ChatMessage> Flatten(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ConversationTurnRecord> turns,
        IReadOnlyList<ToolExecutionRecord> toolExecutions,
        string agentInstructions,
        IReadOnlyList<string> systemInstructions,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        DirectTurnExecutionContext executionContext,
        ConversationCompactionRecord? compression = null,
        IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>? attachments = null)
    {
        var build = DirectHistoryReplay.Compose(
            DirectHistoryReplay.BuildUnits(messages, turns, toolExecutions, messageAdjustments, attachments),
            DirectSystemPrompt.BuildSystemText(agentInstructions, systemInstructions, executionContext, compression),
            DirectSystemPrompt.BuildTrailingProtectedMessages(executionContext),
            compression);

        var result = new List<ChatMessage>();
        if (build.SystemText is { } systemText)
        {
            result.Add(new ChatMessage(ChatRole.System, systemText));
        }

        foreach (var unit in build.Units)
        {
            result.AddRange(unit.Messages);
        }

        result.AddRange(build.TrailingProtectedMessages);
        return result;
    }
}
