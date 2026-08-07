using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record DirectTurnExecutionContext(
    DirectTurnOrigin Origin,
    DirectCapabilityCeiling? CapabilityCeiling,
    SubagentCompletionBatch? CompletionBatch);
