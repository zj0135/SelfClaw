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
        GitWorkspaceState? gitState = null;
        if (selected is not null && _gitWorkspaceQuery is not null)
        {
            gitState = await _gitWorkspaceQuery.GetStateAsync(selected, cancellationToken).ConfigureAwait(false);
        }

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
            roots = _selectionController.WorkspaceRoots.Select(root => new
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

        return _selectionController.WorkspaceRoots.FirstOrDefault(candidate => candidate.Id == parsedId)
            ?? throw new InvalidOperationException("该工作目录已不在工作区列表中。");
    }

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
