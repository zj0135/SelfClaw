using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;
using SelfClaw.Infrastructure.Tools.Workspace;

namespace SelfClaw.Tests.Desktop.Services.Workspace;

public sealed class WorkspaceSelectionBridgeTests
{
    [Fact]
    public async Task TryHandleAsync_selects_a_path_and_returns_a_correlated_state()
    {
        var controller = new FakeWorkspaceSelectionController();
        var bridge = new WorkspaceSelectionBridge(
            controller,
            new FakeWorkspaceFolderPicker(null),
            new WorkspaceToolService(new(), new(), new()));
        using var request = JsonDocument.Parse("""
            {
              "type": "select-workspace-root",
              "requestId": "workspace-1",
              "rootPath": "E:\\git_repo\\SelfClaw"
            }
            """);

        var response = await bridge.TryHandleAsync(
            "select-workspace-root",
            request.RootElement,
            ownerHandle: 0);

        response.Should().NotBeNull();
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
        var bridge = new WorkspaceSelectionBridge(controller, picker, new WorkspaceToolService(new(), new(), new()));
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

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId)
        {
            _workspaceRoots.RemoveAll(root => root.Id == workspaceRootId);
            if (SelectedWorkspaceRoot?.Id == workspaceRootId)
            {
                SelectedWorkspaceRoot = null;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 添加/删除工作目录会连带产生两个 selection 回包（操作本身 + 随后由会话切换触发的读取）。
    /// 两者描述同一个根，各探测一遍就是同一批 git 进程跑两次——每个仓库根 8 个 git.exe。
    /// 这两条测试把「同一个根只探测一次」钉住，同时保证换根仍然会重新探测。
    /// </summary>
    [Fact]
    public async Task Repeated_selection_reads_probe_git_once_for_the_same_root()
    {
        var controller = new FakeWorkspaceSelectionController();
        await controller.SelectOrAddWorkspaceRootAsync(@"E:\work");
        var query = new CountingWorkspaceQuery();
        var bridge = CreateBridge(controller, query);

        await ReadSelectionAsync(bridge);
        await ReadSelectionAsync(bridge);

        query.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Selecting_another_root_probes_git_again()
    {
        var controller = new FakeWorkspaceSelectionController();
        var first = await controller.SelectOrAddWorkspaceRootAsync(@"E:\work");
        await controller.SelectOrAddWorkspaceRootAsync(@"E:\other");
        var query = new CountingWorkspaceQuery();
        var bridge = CreateBridge(controller, query);

        await ReadSelectionAsync(bridge);
        controller.SelectWorkspaceRoot(first.Id);
        await ReadSelectionAsync(bridge);

        // 复用只按根生效：两次回包描述的是不同的根，所以必须各自探测。
        query.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Overlapping_selection_reads_share_one_git_probe()
    {
        var controller = new FakeWorkspaceSelectionController();
        await controller.SelectOrAddWorkspaceRootAsync(@"E:\work");
        var query = new CountingWorkspaceQuery(hold: true);
        var bridge = CreateBridge(controller, query);

        var first = ReadSelectionAsync(bridge);
        await query.Entered.Task;
        // 第一次探测还在等放手，第二次请求同步走到同一份在途探测上。
        var second = ReadSelectionAsync(bridge);
        query.Release();
        await Task.WhenAll(first, second);

        query.Calls.Should().Be(1);
    }

    private static WorkspaceSelectionBridge CreateBridge(
        FakeWorkspaceSelectionController controller,
        IGitWorkspaceQuery query)
        => new(controller, new FakeWorkspaceFolderPicker(null), new WorkspaceToolService(new(), new(), new()), query);

    private static async Task ReadSelectionAsync(WorkspaceSelectionBridge bridge)
    {
        using var request = JsonDocument.Parse("""
            { "type": "get-workspace-selection", "requestId": "selection-1" }
            """);
        await bridge.TryHandleAsync("get-workspace-selection", request.RootElement, ownerHandle: 0);
    }

    private sealed class CountingWorkspaceQuery : IGitWorkspaceQuery
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public CountingWorkspaceQuery(bool hold = false)
        {
            if (!hold) _gate.TrySetResult();
        }

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>第一次探测进入后完成，用来把第二个请求安排到「探测在途」的时间点上。</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<GitWorkspaceState> GetStateAsync(
            WorkspaceRoot workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GitWorkspaceState(
                true, true, null, Guid.NewGuid(), "repository", workspaceRoot.RootPath,
                "main", "sha", false, false, 0, 0, false, null, null, false, [], []);
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

    /// <summary>
    /// The `workspace-tree/list` half of the bridge, exercised against the real
    /// <see cref="WorkspaceToolService"/> rather than a stub: the listing contract the sidebar tree inherits
    /// (hidden entries, dot-prefixed names, build directories, the entry cap) lives in that service, so these
    /// tests pin the behaviour the tree actually shows.
    /// </summary>
    public sealed class WorkingDirectoryTree : IDisposable
    {
        private readonly string _rootPath;
        private readonly WorkspaceRoot _root;
        private readonly WorkspaceSelectionBridge _bridge;

        public WorkingDirectoryTree()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), $"selfclaw-tree-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootPath);
            Directory.CreateDirectory(Path.Combine(_rootPath, "src"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "src", "nested"));
            File.WriteAllText(Path.Combine(_rootPath, "src", "app.ts"), "export {};");
            File.WriteAllText(Path.Combine(_rootPath, "readme.md"), "# readme");

            var now = DateTimeOffset.UtcNow;
            _root = new WorkspaceRoot(Guid.NewGuid(), "SelfClaw", _rootPath, now, now);
            _bridge = new WorkspaceSelectionBridge(
                new SingleRootController(_root),
                new FakeWorkspaceFolderPicker(null),
                new WorkspaceToolService(new(), new(), new()));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // A locked temp file must not fail the test run.
            }
        }

        [Fact]
        public async Task Lists_the_root_level_with_directories_first()
        {
            var result = await ListAsync(relativePath: null);

            result.GetProperty("ok").GetBoolean().Should().BeTrue();
            result.GetProperty("type").GetString().Should().Be("workspace-tree/list");
            result.GetProperty("rootName").GetString().Should().Be("SelfClaw");
            result.GetProperty("rootPath").GetString().Should().Be(_rootPath);
            result.GetProperty("requestId").GetString().Should().Be("tree-1");

            var names = EntryNames(result);
            names.Should().StartWith("src");
            names.Should().Contain("readme.md");
        }

        [Fact]
        public async Task Hidden_files_and_directories_are_omitted()
        {
            var hiddenFile = Path.Combine(_rootPath, "secret.txt");
            File.WriteAllText(hiddenFile, "hidden");
            File.SetAttributes(hiddenFile, FileAttributes.Hidden);

            var hiddenDirectory = Path.Combine(_rootPath, "hidden-dir");
            Directory.CreateDirectory(hiddenDirectory);
            File.SetAttributes(hiddenDirectory, FileAttributes.Directory | FileAttributes.Hidden);

            var names = EntryNames(await ListAsync(relativePath: null));

            names.Should().NotContain("secret.txt");
            names.Should().NotContain("hidden-dir");
            names.Should().Contain("readme.md");
        }

        // Inherited from ListFilesAsync, which drops dot-prefixed names and build/dependency directories
        // alongside OS-hidden entries.
        [Fact]
        public async Task Dot_prefixed_and_build_directories_are_omitted()
        {
            File.WriteAllText(Path.Combine(_rootPath, ".gitignore"), "bin/");
            Directory.CreateDirectory(Path.Combine(_rootPath, ".vs"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "bin"));
            Directory.CreateDirectory(Path.Combine(_rootPath, "node_modules"));

            var names = EntryNames(await ListAsync(relativePath: null));

            names.Should().NotContain(".gitignore");
            names.Should().NotContain(".vs");
            names.Should().NotContain("bin");
            names.Should().NotContain("node_modules");
            names.Should().Contain("src");
        }

        [Fact]
        public async Task Nested_directories_are_listed_with_root_relative_paths()
        {
            var result = await ListAsync("src");

            result.GetProperty("relativePath").GetString().Should().Be("src");
            var entries = result.GetProperty("entries").EnumerateArray().ToArray();
            entries.Should().HaveCount(2);

            var nested = entries.Single(entry => entry.GetProperty("name").GetString() == "nested");
            nested.GetProperty("isDirectory").GetBoolean().Should().BeTrue();
            nested.GetProperty("relativePath").GetString().Should().Be("src/nested");

            var file = entries.Single(entry => entry.GetProperty("name").GetString() == "app.ts");
            file.GetProperty("isDirectory").GetBoolean().Should().BeFalse();
            file.GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(0);
        }

        // The normalised relativePath a row is keyed on has to be accepted back verbatim when that row expands.
        [Fact]
        public async Task A_returned_relative_path_can_be_listed_again()
        {
            var nested = (await ListAsync("src"))
                .GetProperty("entries")
                .EnumerateArray()
                .Single(entry => entry.GetProperty("name").GetString() == "nested")
                .GetProperty("relativePath")
                .GetString();

            var result = await ListAsync(nested);

            result.GetProperty("ok").GetBoolean().Should().BeTrue();
            result.GetProperty("relativePath").GetString().Should().Be("src/nested");
        }

        [Fact]
        public async Task A_short_listing_is_not_flagged_as_being_at_the_entry_limit()
        {
            var result = await ListAsync(relativePath: null);

            result.GetProperty("atEntryLimit").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task An_oversized_directory_is_capped_and_flagged()
        {
            var crowded = Path.Combine(_rootPath, "crowded");
            Directory.CreateDirectory(crowded);
            for (var index = 0; index < 260; index++)
            {
                File.WriteAllText(Path.Combine(crowded, $"file-{index:D4}.txt"), "x");
            }

            var result = await ListAsync("crowded");
            var limit = result.GetProperty("entryLimit").GetInt32();

            result.GetProperty("atEntryLimit").GetBoolean().Should().BeTrue();
            result.GetProperty("entries").GetArrayLength().Should().Be(limit);
        }

        [Fact]
        public async Task Path_traversal_outside_the_root_is_refused()
        {
            var result = await ListAsync("../..");

            result.GetProperty("ok").GetBoolean().Should().BeFalse();
            result.GetProperty("error").GetString().Should().Contain("traversal");
        }

        [Fact]
        public async Task An_unknown_workspace_root_id_is_refused()
        {
            var result = await ListAsync(relativePath: null, workspaceRootId: Guid.NewGuid());

            result.GetProperty("ok").GetBoolean().Should().BeFalse();
            result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task A_missing_workspace_root_id_is_refused()
        {
            using var request = JsonDocument.Parse("""
                { "type": "workspace-tree/list", "requestId": "tree-1" }
                """);

            var result = Serialize(await _bridge.TryHandleAsync(
                "workspace-tree/list",
                request.RootElement,
                ownerHandle: 0));

            result.GetProperty("ok").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task A_missing_directory_is_reported_as_an_error_rather_than_an_empty_listing()
        {
            var result = await ListAsync("does-not-exist");

            result.GetProperty("ok").GetBoolean().Should().BeFalse();
            result.GetProperty("relativePath").GetString().Should().Be("does-not-exist");
        }

        private async Task<JsonElement> ListAsync(string? relativePath, Guid? workspaceRootId = null)
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "workspace-tree/list",
                ["requestId"] = "tree-1",
                ["workspaceRootId"] = (workspaceRootId ?? _root.Id).ToString("D"),
                ["relativePath"] = relativePath
            }));

            return Serialize(await _bridge.TryHandleAsync(
                "workspace-tree/list",
                request.RootElement,
                ownerHandle: 0));
        }

        // The router hands responses to WebViewHostChannel, which serialises camelCase; round-tripping here
        // asserts the shape the frontend actually receives.
        private static JsonElement Serialize(object? response)
        {
            response.Should().NotBeNull();
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private static string[] EntryNames(JsonElement result)
            => [.. result.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)];

        private sealed class SingleRootController(WorkspaceRoot root) : IWorkspaceSelectionController
        {
            public WorkspaceRoot? SelectedWorkspaceRoot => root;

            public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => [root];

            public Task ReloadWorkspaceSelectionAsync() => Task.CompletedTask;

            public void SelectWorkspaceRoot(Guid? workspaceRootId) { }

            public Task<WorkspaceRoot> SelectOrAddWorkspaceRootAsync(string rootPath) => Task.FromResult(root);

            public Task DeleteWorkspaceRootAsync(Guid workspaceRootId) => Task.CompletedTask;
        }
    }
}
