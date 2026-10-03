using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record ConversationTurnExecutionResult(
    ConversationTurnRecord? Turn,
    bool Persisted,
    string? ErrorMessage = null);
