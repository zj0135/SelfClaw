using System.IO;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.Services.Workspace;

internal sealed class WorkspaceSelectionBridge
{
    private readonly IWorkspaceSelectionController _selectionController;
    private readonly IWorkspaceFolderPicker _folderPicker;
    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly IGitWorkspaceQuery? _gitWorkspaceQuery;

    public WorkspaceSelectionBridge(
        IWorkspaceSelectionController selectionController,
        IWorkspaceFolderPicker folderPicker,
        IWorkspaceToolService workspaceToolService,
        IGitWorkspaceQuery? gitWorkspaceQuery = null)
    {
        _selectionController = selectionController;
        _folderPicker = folderPicker;
        _workspaceToolService = workspaceToolService;
        _gitWorkspaceQuery = gitWorkspaceQuery;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        nint ownerHandle,
        CancellationToken cancellationToken = default)
    {
        if (type is WorkspaceTreeMessageType)
        {
            return await ListWorkspaceTreeAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        if (type is not ("get-workspace-selection" or "select-workspace-root" or "browse-workspace-folder" or "delete-workspace-root"))
        {
            return null;
        }

        var requestId = ReadOptionalString(payload, "requestId");
        try
        {
            switch (type)
            {
                case "get-workspace-selection":
                    if (ReadBoolean(payload, "refresh") || _selectionController.WorkspaceRoots.Count == 0)
                    {
                        await _selectionController.ReloadWorkspaceSelectionAsync();
                    }
                    break;
                case "select-workspace-root":
                    await SelectWorkspaceRootAsync(payload);
                    break;
                case "browse-workspace-folder":
                {
                    var selectedPath = _folderPicker.PickFolder(ownerHandle, ResolveInitialPickerDirectory());
                    if (string.IsNullOrWhiteSpace(selectedPath))
                    {
                        return await BuildStateResponseAsync(
                            requestId,
                            cancelled: true,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    await _selectionController.SelectOrAddWorkspaceRootAsync(selectedPath);
                    break;
                }
                case "delete-workspace-root":
                {
                    var workspaceRootId = ReadOptionalString(payload, "workspaceRootId");
                    if (Guid.TryParse(workspaceRootId, out var parsedId) && parsedId != Guid.Empty)
                    {
                        await _selectionController.DeleteWorkspaceRootAsync(parsedId);
                    }

                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await BuildStateResponseAsync(requestId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await BuildStateResponseAsync(requestId, error: exception.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SelectWorkspaceRootAsync(JsonElement payload)
    {
        var rootPath = ReadOptionalString(payload, "rootPath");
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            await _selectionController.SelectOrAddWorkspaceRootAsync(rootPath);
            return;
        }

        var workspaceRootId = ReadOptionalString(payload, "workspaceRootId");
        _selectionController.SelectWorkspaceRoot(
            Guid.TryParse(workspaceRootId, out var parsedWorkspaceRootId)
                ? parsedWorkspaceRootId
                : null);
    }

    private async Task<object> BuildStateResponseAsync(
        string? requestId,
        bool? cancelled = null,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var selected = _selectionController.SelectedWorkspaceRoot;
        var roots = _selectionController.WorkspaceRoots;
        var gitState = selected is null || _gitWorkspaceQuery is null
            ? null
            : await ResolveGitStateAsync(selected, cancellationToken).ConfigureAwait(false);

        var currentPath = selected?.RootPath;
        var currentIsFallback = false;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            currentPath = ResolveDefaultDirectory();
            currentIsFallback = true;
        }

        return new
        {
            type = "workspace-selection",
            requestId,
            cancelled,
            error,
            current = new
            {
                id = selected?.Id.ToString("D"),
                name = selected?.Name ?? ResolveDirectoryDisplayName(currentPath),
                path = currentPath,
                isFallback = currentIsFallback,
                git = gitState,
                repositoryName = gitState?.RepositoryName ?? selected?.GitRepositoryName,
                branchName = gitState?.BranchName ?? selected?.GitBranchName,
                isGitRepository = gitState?.IsRepository == true || selected?.GitRepositoryId is not null,
                isManagedWorktree = gitState?.IsManagedWorktree == true || selected?.IsManagedWorktree == true,
                isDirty = gitState?.IsDirty == true,
                hasMergeConflicts = gitState?.HasMergeConflicts == true
            },
            roots = roots.Select(root => new
            {
                id = root.Id.ToString("D"),
                root.Name,
                path = root.RootPath,
                selected = selected?.Id == root.Id,
                repositoryId = root.GitRepositoryId?.ToString("D"),
                repositoryName = root.GitRepositoryName,
                branchName = root.GitBranchName,
                isManagedWorktree = root.IsManagedWorktree,
                managedConversationId = root.ManagedConversationId?.ToString("D")
            }).ToArray(),
            commonFolders = BuildCommonFolders()
        };
    }

    #region Git state reuse

    // 一次「添加/删除工作目录」会连着产生两个 selection 回包：操作本身那个，和紧接着由会话切换
    // 触发的一次读取。两者描述的是同一个根，各自探测一遍就是同一批 git 进程跑两次——每多一个仓库根
    // 多 8 个 git.exe、约一秒。这里按「根」去重，只影响 git 探测，不改变回包描述的是哪个根：
    //   · 并发的那次直接等同一次探测的结果，不再另起进程；
    //   · 紧随其后的那次在短暂窗口内复用刚拿到的状态。
    // 换根、删除根都不受影响（状态以根 id 为键），显式刷新走 get-git-state，不经过这里。
    private const long GitStateReuseWindowMs = 1000;

    private readonly object _gitStateGate = new();
    private readonly Dictionary<Guid, GitStateProbe> _gitStateProbes = [];

    private sealed record GitStateProbe(Task<GitWorkspaceState>? InFlight, GitWorkspaceState? State, long ProbedAt);

    private Task<GitWorkspaceState> ResolveGitStateAsync(WorkspaceRoot root, CancellationToken cancellationToken)
    {
        Task<GitWorkspaceState> probe;
        lock (_gitStateGate)
        {
            PruneGitStateProbes();
            var existing = _gitStateProbes.GetValueOrDefault(root.Id);
            if (existing?.InFlight is { } inFlight)
            {
                probe = inFlight;
            }
            else if (existing?.State is { } reusable &&
                     Environment.TickCount64 - existing.ProbedAt < GitStateReuseWindowMs)
            {
                return Task.FromResult(reusable);
            }
            else
            {
                probe = StartGitStateProbe(root);
            }
        }

        // 共用的探测跑在 CancellationToken.None 上，每个等待者各自按自己的令牌取消，
        // 于是一个调用方取消不会连带让另一个调用方失败，也不会中断已在跑的 git 进程。
        return cancellationToken.CanBeCanceled ? probe.WaitAsync(cancellationToken) : probe;
    }

    private Task<GitWorkspaceState> StartGitStateProbe(WorkspaceRoot root)
    {
        var completion = new TaskCompletionSource<GitWorkspaceState>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 先登记再启动：探测可能在启动过程中同步完成，反过来的顺序会把已写好的结果盖回「进行中」。
        _gitStateProbes[root.Id] = new GitStateProbe(completion.Task, null, Environment.TickCount64);
        _ = RunGitStateProbeAsync(root, completion);
        return completion.Task;
    }

    private async Task RunGitStateProbeAsync(WorkspaceRoot root, TaskCompletionSource<GitWorkspaceState> completion)
    {
        try
        {
            var state = await _gitWorkspaceQuery!.GetStateAsync(root, CancellationToken.None).ConfigureAwait(false);
            lock (_gitStateGate) _gitStateProbes[root.Id] = new GitStateProbe(null, state, Environment.TickCount64);
            completion.TrySetResult(state);
        }
        catch (Exception exception)
        {
            // 失败不进缓存：下一个请求应该重新探测，而不是继承这次错误。
            lock (_gitStateGate) _gitStateProbes.Remove(root.Id);
            completion.TrySetException(exception);
        }
    }

    private void PruneGitStateProbes()
    {
        if (_gitStateProbes.Count == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        var expired = _gitStateProbes
            .Where(entry => entry.Value.InFlight is null && now - entry.Value.ProbedAt >= GitStateReuseWindowMs)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var workspaceRootId in expired)
        {
            _gitStateProbes.Remove(workspaceRootId);
        }
    }

    #endregion

    #region Working-directory tree

    private const string WorkspaceTreeMessageType = "workspace-tree/list";

    // Mirrors WorkspaceToolService's own listing cap. It is not observable from the returned list, so a
    // full page is reported as "at the limit" rather than guessed to be complete.
    private const int TreeEntryLimit = 250;

    /// <summary>
    /// Serves the sidebar's working-directory tree, one directory level per request.
    ///
    /// The root is addressed by id and looked up in <see cref="IWorkspaceSelectionController.WorkspaceRoots"/>
    /// rather than taken as a path from the payload, so the reachable set stays closed to roots the user has
    /// already opened. Enumeration is <see cref="IWorkspaceToolService.ListFilesAsync"/> — the same primitive
    /// the agent tools and plugin panels read through — which owns the traversal guard, the entry cap and the
    /// hidden/dot-prefixed/build-directory filtering.
    ///
    /// Unlike the selection messages this answers with its own payload instead of the shared state envelope:
    /// expanding a folder is a per-click operation and <see cref="BuildStateResponseAsync"/> would fork a
    /// handful of git processes each time.
    /// </summary>
    private async Task<object> ListWorkspaceTreeAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var requestId = ReadOptionalString(payload, "requestId");
        var relativePath = ReadOptionalString(payload, "relativePath");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveWorkspaceRootById(payload);
            var entries = await _workspaceToolService
                .ListFilesAsync(root.RootPath, relativePath, cancellationToken)
                .ConfigureAwait(false);

            return new
            {
                type = WorkspaceTreeMessageType,
                requestId,
                ok = true,
                workspaceRootId = root.Id.ToString("D"),
                rootName = root.Name,
                rootPath = root.RootPath,
                relativePath = relativePath ?? string.Empty,
                entries = entries.Select(ToTreeEntry).ToArray(),
                atEntryLimit = entries.Count >= TreeEntryLimit,
                entryLimit = TreeEntryLimit
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new
            {
                type = WorkspaceTreeMessageType,
                requestId,
                ok = false,
                relativePath = relativePath ?? string.Empty,
                error = exception.Message
            };
        }
    }

    private WorkspaceRoot ResolveWorkspaceRootById(JsonElement payload)
    {
        var workspaceRootId = ReadOptionalString(payload, "workspaceRootId");
        if (!Guid.TryParse(workspaceRootId, out var parsedId) || parsedId == Guid.Empty)
        {
            throw new ArgumentException("workspaceRootId is required.");
        }

        return FindWorkspaceRoot(parsedId);
    }

    /// <summary>
    /// 按 id 解析已登记工作区根的绝对路径。和目录树一样，根只在宿主状态里查，
    /// 不接受前端传路径，可打开的范围因此保持在用户已经用过的根之内。
    /// </summary>
    public string ResolveWorkspaceRootPath(Guid workspaceRootId)
    {
        if (workspaceRootId == Guid.Empty)
        {
            throw new ArgumentException("workspaceRootId is required.");
        }

        var root = FindWorkspaceRoot(workspaceRootId);
        if (!Directory.Exists(root.RootPath))
        {
            throw new DirectoryNotFoundException($"工作目录已不存在：{root.RootPath}");
        }

        return root.RootPath;
    }

    private WorkspaceRoot FindWorkspaceRoot(Guid workspaceRootId)
        => _selectionController.WorkspaceRoots.FirstOrDefault(candidate => candidate.Id == workspaceRootId)
            ?? throw new InvalidOperationException("该工作目录已不在工作区列表中。");

    // ListFilesAsync returns paths relative to the root; the leaf segment is the display name and the
    // separator is normalised so the frontend can key rows on it and send it straight back.
    private static object ToTreeEntry(WorkspaceFileEntry entry)
    {
        var relativePath = entry.RelativePath.Replace('\\', '/');
        var separatorIndex = relativePath.LastIndexOf('/');
        return new
        {
            name = separatorIndex >= 0 ? relativePath[(separatorIndex + 1)..] : relativePath,
            relativePath,
            entry.IsDirectory,
            entry.SizeBytes
        };
    }

    #endregion

    private string ResolveInitialPickerDirectory()
    {
        var selectedPath = _selectionController.SelectedWorkspaceRoot?.RootPath;
        if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
        {
            return selectedPath;
        }

        return ResolveDefaultDirectory();
    }

    private static object[] BuildCommonFolders()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath)
            ? []
            :
            [
                new
                {
                    id = "desktop",
                    name = "Desktop",
                    path = desktopPath
                }
            ];
    }

    private static string ResolveDefaultDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath) && Directory.Exists(desktopPath))
        {
            return desktopPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile) ? AppContext.BaseDirectory : userProfile;
    }

    private static string ResolveDirectoryDisplayName(string path)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(normalizedPath);
        return string.IsNullOrWhiteSpace(name) ? normalizedPath : name;
    }

    private static bool ReadBoolean(JsonElement payload, string propertyName)
        => payload.TryGetProperty(propertyName, out var element) &&
           element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
           element.GetBoolean();

    private static string? ReadOptionalString(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
