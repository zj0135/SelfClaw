using System.IO;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class ConversationInputExecutionValidator
{
    private readonly IWorkspaceRootRepository _roots;
    private readonly IAiModelCatalog _models;
    private readonly AgentSettingsService _agents;

    public ConversationInputExecutionValidator(IWorkspaceRootRepository roots, IAiModelCatalog models, AgentSettingsService agents)
    {
        _roots = roots;
        _models = models;
        _agents = agents;
    }

    internal async Task<ConversationInputPreparation> PrepareAsync(ConversationRecord conversation,
        ConversationInputExecutionSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = ConversationInputSnapshots.ReadAgent(_agents, snapshot.AgentId);
        if (current is null) return Unavailable(ConversationInputReason.AgentMissing);
        if (snapshot.Version != 1 || snapshot.AgentDefinition is null || snapshot.DefinitionHash is null ||
            snapshot.Mode != AgentExecutionMode.Direct ||
            ConversationInputSnapshots.Hash(snapshot.AgentDefinition) != snapshot.DefinitionHash ||
            ConversationInputSnapshots.Hash(current) != snapshot.DefinitionHash)
            return Unavailable(ConversationInputReason.AgentChanged);
        if (snapshot.ModelProfileId is not { } modelId || !await _models.IsModelAvailableAsync(modelId, token).ConfigureAwait(false))
            return Unavailable(ConversationInputReason.ModelDisabled);
        if (conversation.WorkspaceRootId != snapshot.WorkspaceRootId)
            return Unavailable(ConversationInputReason.WorkspaceMissing);
        WorkspaceRoot? root = null;
        if (snapshot.WorkspaceRootId is { } rootId)
        {
            root = (await _roots.ListWorkspaceRootsAsync(token).ConfigureAwait(false)).FirstOrDefault(item => item.Id == rootId);
            if (root is null || !string.Equals(root.RootPath, snapshot.WorkspaceRootPath, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(root.RootPath))
                return Unavailable(ConversationInputReason.WorkspaceMissing);
        }
        return new(new PreparedFollowUp(snapshot.AgentDefinition with { Mode = snapshot.Mode }, root), null);
    }

    private static ConversationInputPreparation Unavailable(string reason) => new(null, reason);
}
