using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed record PreparedFollowUp(
    AgentRuntimeDefinition Agent,
    WorkspaceRoot? WorkspaceRoot);
