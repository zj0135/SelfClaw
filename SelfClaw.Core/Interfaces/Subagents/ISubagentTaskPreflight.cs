using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentTaskPreflight
{
    Task<SubagentPreflightFailure?> CheckAsync(SubagentDefinitionSnapshot definition, SubagentTaskStartRequest request,
        Guid resolvedModelProfileId, CancellationToken cancellationToken);
}
