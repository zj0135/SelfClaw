using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class ApprovedAIFunctionTests
{
    [Fact]
    public async Task InvokeAsync_FullAccess_BypassesApproval()
    {
        var handler = new RecordingApprovalHandler(approved: false);
        var function = CreateFunction(ToolPermissionMode.FullAccess, handler);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = "ok" });

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_RequireApproval_PropagatesSourceAndInvokesWhenApproved()
    {
        var handler = new RecordingApprovalHandler(approved: true);
        var function = CreateFunction(ToolPermissionMode.RequireApproval, handler);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = "ok" });

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.SourceKind.Should().Be(ToolSourceKind.Mcp);
        request.SourceId.Should().Be("git");
        request.TransportSummary.Should().Be("stdio: node");
        request.AnnotationsJson.Should().Be("{\"readOnlyHint\":true}");
        request.ArgumentsJson.Should().Contain("ok");
    }

    [Fact]
    public async Task InvokeAsync_WhenRejected_ReturnsCanceledWithoutWritingAnExecutionCheckpoint()
    {
        var checkpoint = new CountingCheckpoint();
        var function = CreateFunction(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: false), checkpoint);

        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = "blocked" });

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        checkpoint.Calls.Should().Be(0);
    }

    private static ApprovedAIFunction CreateFunction(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        IToolExecutionCheckpoint? checkpoint = null)
    {
        var inner = AIFunctionFactory.Create(
            (string value) => value,
            "mcp__git__echo",
            "Echo a value.");
        return new ApprovedAIFunction(
            inner,
            Guid.NewGuid(),
            permissionMode,
            approvalHandler,
            "echo",
            ToolSourceKind.Mcp,
            "git",
            "stdio: node",
            "{\"readOnlyHint\":true}", checkpoint: checkpoint);
    }

    private sealed class CountingCheckpoint : IToolExecutionCheckpoint
    {
        internal int Calls { get; private set; }
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApprovalHandler : IToolApprovalHandler
    {
        private readonly bool _approved;

        public RecordingApprovalHandler(bool approved)
        {
            _approved = approved;
        }

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<bool> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_approved);
        }
    }
}
