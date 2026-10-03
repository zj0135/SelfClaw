using System.IO;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Agents;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class ConversationInputExecutionValidator
{
    private readonly IWorkspaceRootRepository _roots;
    private readonly IAiModelCatalog _models;
    private readonly AgentSettingsService _agents;

    internal ConversationInputExecutionValidator(IWorkspaceRootRepository roots, IAiModelCatalog models, AgentSettingsService agents)
    {
        _roots = roots;
        _models = models;
        _agents = agents;
    }

    internal async Task<PreparedFollowUp> PrepareAsync(ConversationRecord conversation,
        ConversationInputExecutionSnapshot snapshot, CancellationToken token)
    {
        var current = ConversationInputSnapshots.ReadAgent(_agents, snapshot.AgentId)
            ?? throw new InvalidOperationException(ConversationInputReason.AgentMissing);
        if (snapshot.Version != 1 || snapshot.AgentDefinition is null || snapshot.DefinitionHash is null ||
            snapshot.Mode != SelfClaw.Core.Runtime.AgentExecutionMode.Direct ||
            ConversationInputSnapshots.Hash(snapshot.AgentDefinition) != snapshot.DefinitionHash ||
            ConversationInputSnapshots.Hash(current) != snapshot.DefinitionHash)
            throw new InvalidOperationException(ConversationInputReason.AgentChanged);
        if (snapshot.ModelProfileId is not { } modelId || !await _models.IsModelAvailableAsync(modelId, token).ConfigureAwait(false))
            throw new InvalidOperationException(ConversationInputReason.ModelDisabled);
        WorkspaceRoot? root = null;
        if (conversation.WorkspaceRootId != snapshot.WorkspaceRootId)
            throw new InvalidOperationException(ConversationInputReason.WorkspaceMissing);
        if (snapshot.WorkspaceRootId is { } rootId)
        {
            root = (await _roots.ListWorkspaceRootsAsync(token).ConfigureAwait(false)).FirstOrDefault(item => item.Id == rootId);
            if (root is null || !string.Equals(root.RootPath, snapshot.WorkspaceRootPath, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(root.RootPath))
                throw new InvalidOperationException(ConversationInputReason.WorkspaceMissing);
        }
        return new(snapshot.AgentDefinition with { Mode = snapshot.Mode }, root);
    }
}
