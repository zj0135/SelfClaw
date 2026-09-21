using SelfClaw.Tests.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class WorkspaceAgentToolsetTests
{
    [Fact]
    public void CreateTools_exposes_the_bound_workspace_functions()
    {
        var tools = CreateTools(new FakeWorkspaceToolService(),
            CreateWorkspace(),
            Guid.NewGuid(),
            ToolPermissionMode.RequireApproval,
            null);

        tools.Should().AllBeAssignableTo<AIFunction>();
        tools.Cast<AIFunction>().Select(tool => tool.Name).Should().Equal(
            "list_files",
            "glob_files",
            "search_text",
            "read_file",
            "write_file",
            "edit_file",
            "run_shell_command");
        tools.Cast<AIFunction>().Should().OnlyContain(tool => !string.IsNullOrWhiteSpace(tool.Description));
    }

    [Fact]
    public async Task Invoke_accepts_omitted_optional_parameters()
    {
        // AIFunctionFactory treats a parameter without a default value as required, even when it
        // is nullable. Every optional tool parameter must therefore declare a default so a model
        // that leaves the documented "leave unset" parameters out still gets a result.
        var service = new FakeWorkspaceToolService();
        var tools = CreateTools(
            service, CreateWorkspace(), Guid.NewGuid(), ToolPermissionMode.FullAccess, null);

        await FindFunction(tools, "list_files").InvokeAsync(new AIFunctionArguments());
        await FindFunction(tools, "glob_files").InvokeAsync(
            new AIFunctionArguments { ["pattern"] = "*.cs" });
        await FindFunction(tools, "search_text").InvokeAsync(
            new AIFunctionArguments { ["query"] = "needle" });
        await FindFunction(tools, "read_file").InvokeAsync(
            new AIFunctionArguments { ["relativePath"] = "a.txt" });
        await FindFunction(tools, "edit_file").InvokeAsync(new AIFunctionArguments
        {
            ["relativePath"] = "a.txt",
            ["oldText"] = "a",
            ["newText"] = "b"
        });

        service.EditCalls.Should().ContainSingle().Which.ReplaceAll.Should().BeFalse();
    }

    [Fact]
    public async Task Approved_write_executes_and_carries_conversation_and_arguments()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = true };
        var workspace = CreateWorkspace();
        var conversationId = Guid.NewGuid();
        var function = FindFunction(
            CreateTools(service,
                workspace,
                conversationId,
                ToolPermissionMode.RequireApproval,
                approval),
            "write_file");

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["relativePath"] = "src/new.txt",
            ["content"] = "hello"
        });

        service.WriteCalls.Should().ContainSingle().Which.Should().Be((workspace.RootPath, "src/new.txt", "hello"));
        approval.Requests.Should().ContainSingle();
        approval.Requests[0].ToolName.Should().Be("write_file");
        approval.Requests[0].ConversationId.Should().Be(conversationId);
        using var arguments = JsonDocument.Parse(approval.Requests[0].ArgumentsJson);
        arguments.RootElement.GetProperty("relativePath").GetString().Should().Be("src/new.txt");
        arguments.RootElement.GetProperty("content").GetString().Should().Be("hello");
        result.Should().BeOfType<DirectToolResult>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_or_missing_approval_does_not_execute_write(bool useHandler)
    {
        var service = new FakeWorkspaceToolService();
        var approval = useHandler ? new FakeApprovalHandler { Approved = false } : null;
        var function = FindFunction(
            CreateTools(service,
                CreateWorkspace(),
                Guid.NewGuid(),
                ToolPermissionMode.RequireApproval,
                approval),
            "write_file");

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["relativePath"] = "denied.txt",
            ["content"] = "blocked"
        });

        service.WriteCalls.Should().BeEmpty();
        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(SelfClaw.Core.Runtime.Agent.ToolCallStatus.Canceled);
    }

    [Fact]
    public async Task FullAccess_bypasses_approval_for_shell_commands()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = false };
        var workspace = CreateWorkspace();
        var function = FindFunction(
            CreateTools(service,
                workspace,
                Guid.NewGuid(),
                ToolPermissionMode.FullAccess,
                approval),
            "run_shell_command");

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["command"] = "dotnet test",
            ["timeoutSeconds"] = 90
        });

        approval.Requests.Should().BeEmpty();
        service.ShellCalls.Should().ContainSingle().Which.Should().Be((workspace.RootPath, "dotnet test", 90));
    }

    [Fact]
    public async Task Approved_edit_executes_and_forwards_snippets()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = true };
        var workspace = CreateWorkspace();
        var function = FindFunction(
            CreateTools(service,
                workspace,
                Guid.NewGuid(),
                ToolPermissionMode.RequireApproval,
                approval),
            "edit_file");

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["relativePath"] = "src/app.cs",
            ["oldText"] = "var x = 1;",
            ["newText"] = "var x = 2;",
            ["replaceAll"] = false
        });

        approval.Requests.Should().ContainSingle().Which.ToolName.Should().Be("edit_file");
        service.EditCalls.Should().ContainSingle()
            .Which.Should().Be((workspace.RootPath, "src/app.cs", "var x = 1;", "var x = 2;", false));
    }

    [Fact]
    public async Task Rejected_edit_does_not_execute()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = false };
        var function = FindFunction(
            CreateTools(service,
                CreateWorkspace(),
                Guid.NewGuid(),
                ToolPermissionMode.RequireApproval,
                approval),
            "edit_file");

        var result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["relativePath"] = "src/app.cs",
            ["oldText"] = "a",
            ["newText"] = "b",
            ["replaceAll"] = false
        });

        service.EditCalls.Should().BeEmpty();
        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(SelfClaw.Core.Runtime.Agent.ToolCallStatus.Canceled);
    }

    private static IReadOnlyList<AITool> CreateTools(FakeWorkspaceToolService service, WorkspaceRoot workspace,
        Guid conversationId, ToolPermissionMode mode, IToolApprovalHandler? approval)
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid(), workspace);
        request = request with { ConversationId = conversationId, ToolPermissionMode = mode, ToolApprovalHandler = approval };
        return DirectTurnCapabilityResolver.BindTools(request, new WorkspaceAgentToolset(service).CreateTools(workspace))
            .Select(binding => (AITool)binding.Tool).ToArray();
    }

    private static WorkspaceRoot CreateWorkspace()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRoot(Guid.NewGuid(), "SelfClaw", "E:\\repo\\SelfClaw", now, now);
    }

    private static AIFunction FindFunction(IReadOnlyList<AITool> tools, string name)
        => tools.Cast<AIFunction>().Single(tool => tool.Name == name);

    private sealed class FakeApprovalHandler : IToolApprovalHandler
    {
        public bool Approved { get; init; }
        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<bool> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Approved);
        }
    }

    private sealed class FakeWorkspaceToolService : IWorkspaceToolService
    {
        public List<(string Root, string Path, string Content)> WriteCalls { get; } = [];
        public List<(string Root, string Path, string OldText, string NewText, bool ReplaceAll)> EditCalls { get; } = [];
        public List<(string Root, string Command, int Timeout)> ShellCalls { get; } = [];

        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
            string root,
            string? relativePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);

        public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(
            string root,
            string pattern,
            string? relativePath = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);

        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
            string root,
            string query,
            WorkspaceSearchOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSearchHit>>([]);

        public Task<WorkspaceFileContent> ReadFileAsync(
            string root,
            string relativePath,
            int? startLine = null,
            int? lineCount = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceFileContent(relativePath, "content", false));

        public Task<WorkspaceFileWriteResult> EditFileAsync(
            string root,
            string relativePath,
            string oldText,
            string newText,
            bool replaceAll = false,
            CancellationToken cancellationToken = default)
        {
            EditCalls.Add((root, relativePath, oldText, newText, replaceAll));
            return Task.FromResult(new WorkspaceFileWriteResult(relativePath, true, true, newText.Length, "edited"));
        }

        public Task<WorkspaceFileWriteResult> WriteFileAsync(
            string root,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            WriteCalls.Add((root, relativePath, content));
            return Task.FromResult(new WorkspaceFileWriteResult(relativePath, true, false, content.Length, "written"));
        }

        public Task<ShellCommandResult> RunShellCommandAsync(
            string root,
            string command,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            ShellCalls.Add((root, command, timeoutSeconds));
            return Task.FromResult(new ShellCommandResult(command, true, 0, "ok", "", false, "done"));
        }
    }
}
