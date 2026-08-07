namespace SelfClaw.Core.Models;

public sealed record SubagentDeliveryResolutionResult(
    bool LeaseMatched,
    IReadOnlyList<Guid> DeliveredDeliveryIds,
    IReadOnlyList<Guid> PendingDeliveryIds,
    IReadOnlyList<Guid> DeadLetteredDeliveryIds);
