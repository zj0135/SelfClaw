namespace SelfClaw.Core.Models;

public sealed record ManagedGitWorktreeCreation(
    WorkspaceRoot WorkspaceRoot,
    GitRepositoryRecord Repository,
    GitCheckoutRecord Checkout,
    bool BaseWorkspaceWasDirty);
