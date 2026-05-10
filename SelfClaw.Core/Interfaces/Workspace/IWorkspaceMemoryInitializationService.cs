using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IWorkspaceMemoryInitializationService
{
    bool AgentsFileExists(WorkspaceRoot workspaceRoot);

    Task<WorkspaceFileWriteResult> InitializeAsync(
        WorkspaceRoot workspaceRoot,
        ProviderProfile profile,
        string apiKey,
        CancellationToken cancellationToken = default);
}
