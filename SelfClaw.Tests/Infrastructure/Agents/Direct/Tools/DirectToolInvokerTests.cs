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

public sealed class DirectToolInvokerTests
{
    [Fact]
    public async Task InvokeAsync_FullAccess_BypassesApproval()
    {
        var handler = new RecordingApprovalHandler(approved: false);
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, handler);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_RequireApproval_PropagatesSourceAndInvokesWhenApproved()
    {
        var handler = new RecordingApprovalHandler(approved: true);
        var pipeline = CreatePipeline(ToolPermissionMode.RequireApproval, handler);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.ToolName.Should().Be("mcp__git__echo");
        request.DisplayName.Should().Be("echo");
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
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: false),
            _ => checkpoint);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        checkpoint.Calls.Should().Be(0);
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutApprovalHandler_DeniesWithoutWritingAnExecutionCheckpoint()
    {
        var checkpoint = new CountingCheckpoint();
        var pipeline = CreatePipeline(ToolPermissionMode.RequireApproval, approvalHandler: null, _ => checkpoint);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<DirectToolResult>().Which.Status.Should().Be(ToolCallStatus.Canceled);
        checkpoint.Calls.Should().Be(0);
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_RequiresApprovalFalse_SkipsApproval()
    {
        var handler = new RecordingApprovalHandler(approved: false);
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            handler,
            requiresApproval: false);

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WritesTheCheckpointBeforeExecutingTheTool()
    {
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: true),
            order => new RecordingCheckpoint(order));

        var result = await pipeline.InvokeAsync();

        result.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("ok");
        pipeline.Order.Should().Equal("checkpoint", "tool");
    }

    [Fact]
    public async Task InvokeAsync_WhenTheCheckpointThrows_DoesNotExecuteTheTool()
    {
        var pipeline = CreatePipeline(
            ToolPermissionMode.RequireApproval,
            new RecordingApprovalHandler(approved: true),
            _ => new ThrowingCheckpoint());

        var action = () => pipeline.InvokeAsync().AsTask();

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("checkpoint failed");
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_PropagatesCancellationWithoutInvokingTheTool()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var action = () => pipeline.InvokeAsync(cancellation.Token).AsTask();

        await action.Should().ThrowAsync<OperationCanceledException>();
        pipeline.Order.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_UnboundToolName_Throws()
    {
        var pipeline = CreatePipeline(ToolPermissionMode.FullAccess, approvalHandler: null);
        var unbound = AIFunctionFactory.Create((Func<string>)(() => "ok"), "other", "Unbound tool.");

        var action = () => pipeline.Invoker
            .InvokeAsync(
                new FunctionInvocationContext
                {
                    Function = unbound,
                    Arguments = new AIFunctionArguments { ["value"] = "ok" },
                    CallContent = new FunctionCallContent("call-1", "other", new AIFunctionArguments())
                },
                CancellationToken.None)
            .AsTask();

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*other*");
        pipeline.Order.Should().BeEmpty();
    }

    private static TestPipeline CreatePipeline(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        Func<List<string>, IToolExecutionCheckpoint>? checkpointFactory = null,
        bool requiresApproval = true)
    {
        var order = new List<string>();
        var function = AIFunctionFactory.Create(
            (string value) =>
            {
                order.Add("tool");
                return value;
            },
            "mcp__git__echo",
            "Echo a value.");
        var binding = new DirectToolBinding(
            function,
            new DirectToolDescriptor("mcp__git__echo", ToolCallKind.Run, ToolSourceKind.Mcp, "git", "echo"),
            requiresApproval,
            "stdio: node",
            "{\"readOnlyHint\":true}");
        var request = CreateRequest(permissionMode, approvalHandler, checkpointFactory?.Invoke(order));
        var invoker = new DirectToolInvoker(request, new Dictionary<string, DirectToolBinding>(StringComparer.Ordinal)
        {
            [function.Name] = binding
        });
        return new TestPipeline(invoker, function, order);
    }

    private static DirectChatTurnRequest CreateRequest(
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler,
        IToolExecutionCheckpoint? checkpoint)
    {
        var now = DateTimeOffset.UtcNow;
        return new DirectChatTurnRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            new AgentRuntimeDefinition(
                "direct-test", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], "Follow project instructions."),
            [],
            null,
            permissionMode,
            approvalHandler,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null),
            ToolExecutionCheckpoint: checkpoint);
    }

    private sealed class TestPipeline(DirectToolInvoker invoker, AIFunction function, List<string> order)
    {
        internal DirectToolInvoker Invoker { get; } = invoker;
        internal List<string> Order { get; } = order;

        internal ValueTask<object?> InvokeAsync(CancellationToken cancellationToken = default)
        {
            var arguments = new AIFunctionArguments { ["value"] = "ok" };
            return Invoker.InvokeAsync(
                new FunctionInvocationContext
                {
                    Function = function,
                    Arguments = arguments,
                    CallContent = new FunctionCallContent("call-1", function.Name, arguments)
                },
                cancellationToken);
        }
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

    private sealed class RecordingCheckpoint(List<string> order) : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
        {
            order.Add("checkpoint");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCheckpoint : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("checkpoint failed");
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
