using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Workspace;

public sealed class WorkspaceSelectionBridgeTests
{
    [Fact]
    public async Task TryHandleAsync_selects_a_path_and_returns_a_correlated_state()
    {
        var controller = new FakeWorkspaceSelectionController();
        var bridge = new WorkspaceSelectionBridge(controller, new FakeWorkspaceFolderPicker(null));
        object? response = null;
        bridge.ResponseReady += payload => response = payload;
        using var request = JsonDocument.Parse("""
            {
              "type": "select-workspace-root",
              "requestId": "workspace-1",
              "rootPath": "E:\\git_repo\\SelfClaw"
            }
            """);

        var handled = await bridge.TryHandleAsync(
            "select-workspace-root",
            request.RootElement,
            ownerHandle: 0);

        handled.Should().BeTrue();
        controller.SelectedPath.Should().Be("E:\\git_repo\\SelfClaw");
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(response));
        result.RootElement.GetProperty("requestId").GetString().Should().Be("workspace-1");
        result.RootElement.GetProperty("type").GetString().Should().Be("workspace-selection");
    }

    [Fact]
    public async Task TryHandleAsync_browse_uses_the_folder_picker_adapter()
    {
        var controller = new FakeWorkspaceSelectionController();
        var picker = new FakeWorkspaceFolderPicker("E:\\work");
        var bridge = new WorkspaceSelectionBridge(controller, picker);
        using var request = JsonDocument.Parse("""
            { "type": "browse-workspace-folder", "requestId": "browse-1" }
            """);

        await bridge.TryHandleAsync("browse-workspace-folder", request.RootElement, ownerHandle: 42);

        picker.OwnerHandle.Should().Be(42);
        controller.SelectedPath.Should().Be("E:\\work");
    }

    private sealed class FakeWorkspaceSelectionController : IWorkspaceSelectionController
    {
        private readonly List<WorkspaceRoot> _workspaceRoots = [];

        public WorkspaceRoot? SelectedWorkspaceRoot { get; private set; }

        public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => _workspaceRoots;

        public string? SelectedPath { get; private set; }

        public Task ReloadWorkspaceSelectionAsync() => Task.CompletedTask;

        public void SelectWorkspaceRoot(Guid? workspaceRootId)
        {
            SelectedWorkspaceRoot = workspaceRootId is Guid id
                ? _workspaceRoots.FirstOrDefault(root => root.Id == id)
                : null;
        }

        public Task<WorkspaceRoot> SelectOrAddWorkspaceRootAsync(string rootPath)
        {
            SelectedPath = rootPath;
            var now = DateTimeOffset.UtcNow;
            var root = new WorkspaceRoot(Guid.NewGuid(), "Workspace", rootPath, now, now);
            _workspaceRoots.Add(root);
            SelectedWorkspaceRoot = root;
            return Task.FromResult(root);
        }
    }

    private sealed class FakeWorkspaceFolderPicker(string? selectedPath) : IWorkspaceFolderPicker
    {
        public nint OwnerHandle { get; private set; }

        public string? PickFolder(nint ownerHandle, string initialDirectory)
        {
            OwnerHandle = ownerHandle;
            return selectedPath;
        }
    }
}
