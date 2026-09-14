using SelfClaw.Desktop.Services.Workspace.Models;
using System.IO;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Workspace;

internal sealed class ConversationWorkspaceService
{
    private readonly IWorkspaceRootRepository _roots;
    private readonly IGitWorkspaceManager? _manager;
    private readonly IGitWorkspaceQuery? _query;
    private readonly IGitWorkspaceStore? _store;

    public ConversationWorkspaceService(IWorkspaceRootRepository roots, IGitWorkspaceManager? manager = null,
        IGitWorkspaceQuery? query = null, IGitWorkspaceStore? store = null)
    {
        _roots = roots;
        _manager = manager;
        _query = query;
        _store = store;
    }

    public async Task<IReadOnlyList<WorkspaceRoot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var roots = await _roots.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false);
        if (_query is null) return roots;
        foreach (var root in roots)
            await _query.GetStateAsync(root, cancellationToken).ConfigureAwait(false);
        return await _roots.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceRoot> AddAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath.Trim()));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"The selected workspace directory does not exist: {path}");
        var roots = await _roots.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false);
        var existing = roots.FirstOrDefault(root => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.RootPath)), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var now = DateTimeOffset.UtcNow;
        var name = Path.GetFileName(path);
        return await _roots.UpsertWorkspaceRootAsync(new WorkspaceRoot(Guid.NewGuid(),
            string.IsNullOrWhiteSpace(name) ? path : name, path, now, now), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedConversationWorkspace> PrepareAsync(ConversationRecord? conversation,
        WorkspaceRoot? root, GitWorkspaceMode mode, string prompt, CancellationToken cancellationToken = default)
    {
        if (conversation is null && root?.IsManagedWorktree == true)
            throw new InvalidOperationException("该工作树已绑定其他会话，请选择基础工作目录后新建会话。");
        if (mode != GitWorkspaceMode.ManagedWorktree)
            return new(root, conversation?.Id, false);
        if (root is null) throw new InvalidOperationException("请先选择一个 Git 工作目录。");
        if (conversation is not null)
        {
            if (!root.IsManagedWorktree)
                throw new InvalidOperationException("现有本地会话不能切换为工作树，请新建会话。");
            return new(root, conversation.Id, false);
        }
        if (_manager is null) throw new InvalidOperationException("Git 工作树功能当前不可用。");
        var conversationId = Guid.NewGuid();
        var creation = await _manager.CreateManagedWorktreeAsync(root, conversationId, prompt, cancellationToken).ConfigureAwait(false);
        return new(creation.WorkspaceRoot, conversationId, true);
    }

    public async Task DiscardAsync(PreparedConversationWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.Provisioned || workspace.WorkspaceRoot is not { } root || _manager is null) return;
        await _manager.RemoveManagedWorktreeAsync(root, cancellationToken).ConfigureAwait(false);
        await _roots.DeleteWorkspaceRootAsync(root.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(Guid conversationId, bool removeWorktree, CancellationToken cancellationToken = default)
    {
        if (_store is null) return;
        var checkout = await _store.GetConversationCheckoutAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (checkout is null) return;
        if (!removeWorktree)
        {
            await _store.ReleaseConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var roots = await _roots.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false);
        var root = roots.FirstOrDefault(item => item.Id == checkout.WorkspaceRootId)
            ?? throw new InvalidOperationException("无法找到会话的工作树，请刷新后重试。");
        if (_manager is null) throw new InvalidOperationException("Git 工作树功能当前不可用。");
        await _manager.RemoveManagedWorktreeAsync(root, cancellationToken).ConfigureAwait(false);
        await _roots.DeleteWorkspaceRootAsync(root.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteRootAsync(Guid rootId, CancellationToken cancellationToken = default)
        => _roots.DeleteWorkspaceRootAsync(rootId, cancellationToken);
}
