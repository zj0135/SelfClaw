using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentTaskPreflight
{
    /// <summary>
    /// Decides only the cheap facts that can change while a task is queued: the definition's tool policy
    /// against the captured ceiling, the captured workspace's existence, and the resolved model's
    /// availability. Capability authorization and currency belong to the child turn's capability
    /// resolution, which fails the task when the captured snapshot no longer holds.
    /// </summary>
    Task<SubagentPreflightFailure?> CheckAsync(SubagentDefinitionSnapshot definition, SubagentTaskStartRequest request,
        Guid resolvedModelProfileId, CancellationToken cancellationToken);
}
