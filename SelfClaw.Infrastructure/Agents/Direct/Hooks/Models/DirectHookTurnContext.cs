using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record DirectHookTurnContext(
    Guid TurnId,
    Guid ConversationId,
    DirectTurnOrigin Origin,
    string AgentId,
    string AgentName,
    string? WorkspaceRoot,
    string ProviderName,
    AiProviderKind ProviderKind,
    string Model);
