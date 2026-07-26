using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Extensions.Runtime;

namespace SelfClaw.Tests.Infrastructure.Extensions;

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
    public async Task InvokeAsync_WhenRejected_ReturnsWorkspaceDeniedResult()
    {
        var function = CreateFunction(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: false));

        var result = await function.InvokeAsync(new AIFunctionArguments { ["value"] = "blocked" });

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be(WorkspaceAgentToolset.DeniedResult);
    }

    private static ApprovedAIFunction CreateFunction(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler)
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
            "{\"readOnlyHint\":true}");
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
