using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record AdmittedConversationTurn(
    DesktopConversationTurnRequest Request,
    ConversationRecord Conversation,
    ConversationRuntimeState RuntimeState);
