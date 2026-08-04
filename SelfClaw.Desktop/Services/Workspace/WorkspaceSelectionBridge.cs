using System.IO;
using System.Text.Json;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Desktop.Services.Workspace;

internal sealed class WorkspaceSelectionBridge
{
    private readonly IWorkspaceSelectionController _selectionController;
    private readonly IWorkspaceFolderPicker _folderPicker;

    public WorkspaceSelectionBridge(
        IWorkspaceSelectionController selectionController,
        IWorkspaceFolderPicker folderPicker)
    {
        _selectionController = selectionController;
        _folderPicker = folderPicker;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        nint ownerHandle,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("get-workspace-selection" or "select-workspace-root" or "browse-workspace-folder"))
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
                        return BuildStateResponse(requestId, cancelled: true);
                    }

                    await _selectionController.SelectOrAddWorkspaceRootAsync(selectedPath);
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return BuildStateResponse(requestId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return BuildStateResponse(requestId, error: exception.Message);
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

    private object BuildStateResponse(string? requestId, bool? cancelled = null, string? error = null)
    {
        var selected = _selectionController.SelectedWorkspaceRoot;
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
                isFallback = currentIsFallback
            },
            roots = _selectionController.WorkspaceRoots.Select(root => new
            {
                id = root.Id.ToString("D"),
                root.Name,
                path = root.RootPath,
                selected = selected?.Id == root.Id
            }).ToArray(),
            commonFolders = BuildCommonFolders()
        };
    }

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
