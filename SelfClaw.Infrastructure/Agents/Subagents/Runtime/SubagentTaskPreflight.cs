using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

/// <summary>
/// The Subagent acceptance gate, called once when a task is accepted and again before it executes. Both
/// calls decide the same three facts: whether the definition's tool policy still fits the parent's captured
/// ceiling, whether the captured workspace still exists, and whether the resolved model is still available.
/// The first two are pure comparisons, and the third is the one failure that would otherwise only surface
/// after a provider call.
/// </summary>
/// <remarks>
/// Capability authorization and currency are deliberately not decided here: the child turn's capability
/// resolution enforces them (it is the only place with the loaded package and MCP state), so a queued task
/// that outlives a package or server change fails inside the turn as
/// <c>SubagentErrorCodes.SnapshotInvalid</c> rather than being re-validated from the same repositories a
/// second time.
/// </remarks>
internal sealed class SubagentTaskPreflight : ISubagentTaskPreflight
{
    private readonly IAiModelCatalog _models;

    public SubagentTaskPreflight(IAiModelCatalog models)
    {
        _models = models;
    }

    public async Task<SubagentPreflightFailure?> CheckAsync(SubagentDefinitionSnapshot definition,
        SubagentTaskStartRequest request, Guid resolvedModelProfileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        if (!DirectCapabilityRules.IsToolPolicyAuthorized(definition.ToolPolicy, request.CapabilityCeiling.ToolPolicy))
            return new(SubagentErrorCodes.CapabilityNotAuthorized, "The Subagent tool policy exceeds the parent capability ceiling.");
        if (request.WorkspaceRoot is not null && !Directory.Exists(request.WorkspaceRoot.RootPath))
            return new(SubagentErrorCodes.WorkspaceUnavailable, "The captured workspace is no longer available.");
        if (!await _models.IsModelAvailableAsync(resolvedModelProfileId, cancellationToken).ConfigureAwait(false))
            return new(SubagentErrorCodes.ModelUnavailable, "The selected Subagent model is unavailable.");

        return null;
    }
}
