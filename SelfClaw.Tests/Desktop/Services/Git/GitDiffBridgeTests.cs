using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Git;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace.Abstractions;

namespace SelfClaw.Tests.Desktop.Services.Git;

public sealed class GitDiffBridgeTests
{
    private static readonly WorkspaceRoot Root = new(
        Guid.NewGuid(), "repo", @"C:\repo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Changes_without_a_selected_workspace_fail_with_a_correlated_error()
    {
        var query = new FakeGitDiffQuery();
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(null), query);

        var response = await HandleAsync(bridge, """{"type":"git-diff/changes","requestId":"changes-1"}""");

        response.GetProperty("type").GetString().Should().Be("git-diff/changes");
        response.GetProperty("requestId").GetString().Should().Be("changes-1");
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetString().Should().Contain("工作目录");
        query.ChangesCalls.Should().Be(0);
    }

    [Fact]
    public async Task Changes_are_serialized_with_wire_kinds_and_repository_flags()
    {
        var query = new FakeGitDiffQuery
        {
            Changes = new GitWorkspaceChanges(
                true,
                true,
                null,
                @"C:\repo",
                "main",
                new GitChangeSet(
                [
                    new GitChangedFile("a.txt", null, GitChangeKind.Modified, 1, 0, false),
                    new GitChangedFile("fresh.txt", null, GitChangeKind.Untracked, null, null, false)
                ], false),
                new GitChangeSet([new GitChangedFile("new.txt", "old.txt", GitChangeKind.Renamed, 0, 0, false)], true))
        };
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        var response = await HandleAsync(bridge, """{"type":"git-diff/changes","requestId":"changes-2"}""");

        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.TryGetProperty("error", out _).Should().BeFalse();
        response.GetProperty("workspaceRootId").GetString().Should().Be(Root.Id.ToString("D"));
        response.GetProperty("workspacePath").GetString().Should().Be(@"C:\repo");
        response.GetProperty("isRepository").GetBoolean().Should().BeTrue();
        response.GetProperty("branchName").GetString().Should().Be("main");
        var unstaged = response.GetProperty("unstaged").GetProperty("files");
        unstaged.GetArrayLength().Should().Be(2);
        unstaged[0].GetProperty("kind").GetString().Should().Be("modified");
        unstaged[0].GetProperty("addedLines").GetInt32().Should().Be(1);
        unstaged[1].GetProperty("kind").GetString().Should().Be("untracked");
        unstaged[1].TryGetProperty("addedLines", out _).Should().BeFalse();
        var staged = response.GetProperty("staged");
        staged.GetProperty("atFileLimit").GetBoolean().Should().BeTrue();
        staged.GetProperty("files")[0].GetProperty("oldPath").GetString().Should().Be("old.txt");
        staged.GetProperty("files")[0].GetProperty("kind").GetString().Should().Be("renamed");
    }

    // Not being a repository is the pane's designed empty state: the envelope must resolve, not reject.
    [Fact]
    public async Task Not_a_repository_stays_a_resolvable_envelope_without_an_error_field()
    {
        var query = new FakeGitDiffQuery
        {
            Changes = new GitWorkspaceChanges(
                true, false, "fatal: not a git repository", null, null,
                new GitChangeSet([], false), new GitChangeSet([], false))
        };
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        var response = await HandleAsync(bridge, """{"type":"git-diff/changes","requestId":"changes-3"}""");

        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.GetProperty("isRepository").GetBoolean().Should().BeFalse();
        response.GetProperty("isGitAvailable").GetBoolean().Should().BeTrue();
        response.TryGetProperty("error", out _).Should().BeFalse();
        response.GetProperty("unstaged").GetProperty("files").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task File_requests_forward_scope_path_old_path_and_kind()
    {
        var query = new FakeGitDiffQuery
        {
            FileDiff = new GitFileDiff("new.txt", "old.txt", GitChangeKind.Renamed, false, "patch text", true, 3, 1)
        };
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        var response = await HandleAsync(
            bridge,
            """{"type":"git-diff/file","requestId":"file-1","scope":"staged","path":"new.txt","oldPath":"old.txt","kind":"renamed"}""");

        query.LastFileRequest.Should().Be((GitChangeScope.Staged, "new.txt", "old.txt", GitChangeKind.Renamed));
        response.GetProperty("type").GetString().Should().Be("git-diff/file");
        response.GetProperty("requestId").GetString().Should().Be("file-1");
        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.GetProperty("scope").GetString().Should().Be("staged");
        response.GetProperty("path").GetString().Should().Be("new.txt");
        response.GetProperty("oldPath").GetString().Should().Be("old.txt");
        response.GetProperty("kind").GetString().Should().Be("renamed");
        response.GetProperty("patch").GetString().Should().Be("patch text");
        response.GetProperty("truncated").GetBoolean().Should().BeTrue();
        response.GetProperty("addedLines").GetInt32().Should().Be(3);
        response.GetProperty("removedLines").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task File_requests_default_an_unknown_kind_to_modified()
    {
        var query = new FakeGitDiffQuery();
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        await HandleAsync(bridge, """{"type":"git-diff/file","requestId":"file-2","scope":"unstaged","path":"a.txt","kind":"???"}""");

        query.LastFileRequest.Should().Be((GitChangeScope.Unstaged, "a.txt", (string?)null, GitChangeKind.Modified));
    }

    [Theory]
    [InlineData("""{"type":"git-diff/file","requestId":"file-3","path":"a.txt"}""")]
    [InlineData("""{"type":"git-diff/file","requestId":"file-3","scope":"unstaged"}""")]
    [InlineData("""{"type":"git-diff/file","requestId":"file-3","scope":"all","path":"a.txt"}""")]
    public async Task File_requests_with_invalid_arguments_fail_without_touching_git(string json)
    {
        var query = new FakeGitDiffQuery();
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        var response = await HandleAsync(bridge, json);

        response.GetProperty("type").GetString().Should().Be("git-diff/file");
        response.GetProperty("requestId").GetString().Should().Be("file-3");
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        query.LastFileRequest.Should().BeNull();
    }

    [Fact]
    public async Task Query_failures_become_correlated_failure_envelopes()
    {
        var query = new FakeGitDiffQuery { Failure = new InvalidOperationException("git exploded") };
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), query);

        var response = await HandleAsync(bridge, """{"type":"git-diff/changes","requestId":"changes-4"}""");

        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetString().Should().Be("git exploded");
    }

    [Fact]
    public async Task Other_git_messages_are_left_to_their_own_bridge()
    {
        var bridge = new GitDiffBridge(new FakeWorkspaceSelectionController(Root), new FakeGitDiffQuery());
        using var request = JsonDocument.Parse("""{"type":"git-create-branch","requestId":"x","branchName":"b"}""");

        var response = await bridge.TryHandleAsync("git-create-branch", request.RootElement);

        response.Should().BeNull();
    }

    private static async Task<JsonElement> HandleAsync(GitDiffBridge bridge, string json)
    {
        using var request = JsonDocument.Parse(json);
        var type = request.RootElement.GetProperty("type").GetString() ?? string.Empty;
        var response = await bridge.TryHandleAsync(type, request.RootElement)
            ?? throw new InvalidOperationException($"The bridge did not handle '{type}'.");
        // Serialize through the host channel's options so null dropping matches what the WebView sees.
        using var document = JsonDocument.Parse(WebViewHostChannel.SerializeToUtf8Bytes(response));
        return document.RootElement.Clone();
    }

    private sealed class FakeWorkspaceSelectionController : IWorkspaceSelectionController
    {
        private readonly WorkspaceRoot? _selected;

        public FakeWorkspaceSelectionController(WorkspaceRoot? selected)
        {
            _selected = selected;
        }

        public WorkspaceRoot? SelectedWorkspaceRoot => _selected;

        public IReadOnlyList<WorkspaceRoot> WorkspaceRoots => _selected is null ? [] : [_selected];

        public Task ReloadWorkspaceSelectionAsync() => Task.CompletedTask;

        public void SelectWorkspaceRoot(Guid? workspaceRootId)
        {
        }

        public Task<WorkspaceRoot> SelectOrAddWorkspaceRootAsync(string rootPath) => throw new NotSupportedException();

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId) => Task.CompletedTask;
    }

    private sealed class FakeGitDiffQuery : IGitDiffQuery
    {
        public GitWorkspaceChanges Changes { get; set; } = new(
            true, true, null, @"C:\repo", "main", new GitChangeSet([], false), new GitChangeSet([], false));

        public GitFileDiff FileDiff { get; set; } = new("a.txt", null, GitChangeKind.Modified, false, string.Empty, false, 0, 0);

        public Exception? Failure { get; set; }

        public int ChangesCalls { get; private set; }

        public (GitChangeScope Scope, string Path, string? OldPath, GitChangeKind Kind)? LastFileRequest { get; private set; }

        public Task<GitWorkspaceChanges> GetChangesAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
        {
            ChangesCalls++;
            return Failure is null ? Task.FromResult(Changes) : Task.FromException<GitWorkspaceChanges>(Failure);
        }

        public Task<GitFileDiff> GetFileDiffAsync(
            WorkspaceRoot workspaceRoot,
            GitChangeScope scope,
            string path,
            string? oldPath,
            GitChangeKind kind,
            CancellationToken cancellationToken = default)
        {
            LastFileRequest = (scope, path, oldPath, kind);
            return Failure is null ? Task.FromResult(FileDiff) : Task.FromException<GitFileDiff>(Failure);
        }
    }
}
