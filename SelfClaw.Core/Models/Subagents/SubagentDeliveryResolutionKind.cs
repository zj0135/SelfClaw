namespace SelfClaw.Core.Models;

public enum SubagentDeliveryResolutionKind
{
    Succeeded = 0,
    RetryableFailure = 1,
    UnsafeFailure = 2,
    DeadLetter = 3
}
