using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Desktop.Services.Plugins;

/// <summary>
/// Serves the `plugin-host/api` calls a panel makes through the shell. Two rules hold every op: the
/// permission is checked against the Plugin's acknowledged manifest, and the workspace root comes from
/// the captured context rather than the caller — a panel can never widen its own reach by naming a
/// different root, and the root it reads through <c>getContext()</c> is the same one its file calls
/// resolve against.
/// </summary>
internal sealed class PluginPanelBridge
{
    private const string MessageType = "plugin-host/api";

    private readonly IWorkspaceToolService _workspaceToolService;
    private readonly PluginPanelHostController _hostController;
    private readonly PluginPanelContextPublisher _contextPublisher;

    public PluginPanelBridge(
        IWorkspaceToolService workspaceToolService,
        PluginPanelHostController hostController,
        PluginPanelContextPublisher contextPublisher)
    {
        _workspaceToolService = workspaceToolService;
        _hostController = hostController;
        _contextPublisher = contextPublisher;
    }

    public async Task<object?> TryHandleAsync(
        string type,
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(type, MessageType, StringComparison.Ordinal))
        {
            return null;
        }

        var requestId = ReadString(payload, "requestId");
        try
        {
            var op = ReadString(payload, "op") ?? throw new ArgumentException("op is required.");
            var permissions = _hostController.GetPermissions(ReadString(payload, "panelKey"))
                ?? throw new UnauthorizedAccessException("The calling panel is not open.");
            var args = payload.TryGetProperty("args", out var argsElement) ? argsElement : default;
            return new
            {
                type,
                requestId,
                ok = true,
                result = await ExecuteAsync(op, permissions, args, cancellationToken)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new { type, requestId, ok = false, error = exception.Message };
        }
    }

    private async Task<object?> ExecuteAsync(
        string op,
        IReadOnlyList<string> permissions,
        JsonElement args,
        CancellationToken cancellationToken)
        => op switch
        {
            "context.get" => ReadContext(permissions),
            "workspace.list" => await _workspaceToolService.ListFilesAsync(
                RequireWorkspace(permissions),
                ReadString(args, "relativePath"),
                cancellationToken),
            "workspace.glob" => await _workspaceToolService.GlobFilesAsync(
                RequireWorkspace(permissions),
                ReadString(args, "pattern") ?? throw new ArgumentException("pattern is required."),
                ReadString(args, "relativePath"),
                cancellationToken),
            "workspace.read" => await _workspaceToolService.ReadFileAsync(
                RequireWorkspace(permissions),
                ReadString(args, "relativePath") ?? throw new ArgumentException("relativePath is required."),
                ReadInt32(args, "startLine"),
                ReadInt32(args, "lineCount"),
                cancellationToken),
            "workspace.search" => await _workspaceToolService.SearchTextAsync(
                RequireWorkspace(permissions),
                ReadString(args, "query") ?? throw new ArgumentException("query is required."),
                null,
                cancellationToken),
            _ => throw new ArgumentException($"Unsupported plugin op '{op}'.")
        };

    private object ReadContext(IReadOnlyList<string> permissions)
    {
        Require(permissions, PluginPermissions.ContextRead);
        return _contextPublisher.Capture();
    }

    private string RequireWorkspace(IReadOnlyList<string> permissions)
    {
        Require(permissions, PluginPermissions.WorkspaceRead);
        return _contextPublisher.Capture().WorkspaceRootPath
            ?? throw new InvalidOperationException("No workspace root is selected.");
    }

    private static void Require(IReadOnlyList<string> permissions, string permission)
    {
        if (!PluginPermissions.Grants(permissions, permission))
        {
            throw new UnauthorizedAccessException($"This panel does not declare the '{permission}' permission.");
        }
    }

    private static int? ReadInt32(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object &&
           payload.TryGetProperty(propertyName, out var element) &&
           element.TryGetInt32(out var value)
            ? value
            : null;

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
