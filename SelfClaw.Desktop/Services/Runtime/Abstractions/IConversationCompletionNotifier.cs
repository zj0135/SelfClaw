using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime.Abstractions;

internal interface IConversationCompletionNotifier
{
    void Notify(ConversationRecord conversation, ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages);
}
