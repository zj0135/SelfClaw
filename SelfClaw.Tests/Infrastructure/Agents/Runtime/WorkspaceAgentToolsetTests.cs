using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

public sealed class WorkspaceAgentToolsetTests
{
    [Fact]
    public void CreateTools_exposes_the_five_bound_workspace_functions()
    {
        var tools = CreateToolset(new FakeWorkspaceToolService()).CreateTools(
            CreateWorkspace(),
            Guid.NewGuid(),
            ToolPermissionMode.RequireApproval,
            null);

        tools.Should().AllBeAssignableTo<AIFunction>();
        tools.Cast<AIFunction>().Select(tool => tool.Name).Should().Equal(
            "list_files",
            "search_text",
            "read_file",
            "write_file",
            "run_shell_command");
        tools.Cast<AIFunction>().Should().OnlyContain(tool => !string.IsNullOrWhiteSpace(tool.Description));
    }

    [Fact]
    public async Task Approved_write_executes_and_carries_conversation_and_arguments()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = true };
        var workspace = CreateWorkspace();
        var conversationId = Guid.NewGuid();
        var function = FindFunction(
            CreateToolset(service).CreateTools(
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
        result.Should().BeOfType<JsonElement>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_or_missing_approval_does_not_execute_write(bool useHandler)
    {
        var service = new FakeWorkspaceToolService();
        var approval = useHandler ? new FakeApprovalHandler { Approved = false } : null;
        var function = FindFunction(
            CreateToolset(service).CreateTools(
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
        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be(WorkspaceAgentToolset.DeniedResult);
    }

    [Fact]
    public async Task FullAccess_bypasses_approval_for_shell_commands()
    {
        var service = new FakeWorkspaceToolService();
        var approval = new FakeApprovalHandler { Approved = false };
        var workspace = CreateWorkspace();
        var function = FindFunction(
            CreateToolset(service).CreateTools(
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

    private static WorkspaceAgentToolset CreateToolset(FakeWorkspaceToolService service) => new(service);

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
        public List<(string Root, string Command, int Timeout)> ShellCalls { get; } = [];

        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
            string root,
            string? relativePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);

        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
            string root,
            string query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSearchHit>>([]);

        public Task<WorkspaceFileContent> ReadFileAsync(
            string root,
            string relativePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceFileContent(relativePath, "content", false));

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
