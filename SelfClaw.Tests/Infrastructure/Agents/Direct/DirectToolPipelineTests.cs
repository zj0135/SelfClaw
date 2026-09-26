using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure.Tools.Workspace;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

public sealed class DirectToolPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("shell", ToolCallStatus.Failed, ToolExecutionStatus.Failed)]
    [InlineData("denied-write", ToolCallStatus.Canceled, ToolExecutionStatus.Cancelled)]
    [InlineData("write", ToolCallStatus.Completed, ToolExecutionStatus.Completed)]
    [InlineData("read", ToolCallStatus.Completed, ToolExecutionStatus.Completed)]
    public async Task Real_function_marshalling_preserves_results_through_runtime_and_recorder(
        string scenario, ToolCallStatus expectedStatus, ToolExecutionStatus persistedStatus)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "input.txt"), "file content");
        var name = scenario switch { "shell" => "run_shell_command", "read" => "read_file", _ => "write_file" };
        Dictionary<string, object?> arguments = scenario switch
        {
            "shell" => new() { ["command"] = "exit 7", ["timeoutSeconds"] = 10 },
            "read" => new() { ["relativePath"] = "input.txt", ["startLine"] = null, ["lineCount"] = null },
            _ => new() { ["relativePath"] = "output.txt", ["content"] = "written" }
        };
        var provider = new SingleToolChatClient(name, arguments);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(
            provider,
            pipelineBuilder: (native, pipeline) => new ChatClientBuilder(native)
                .UseFunctionInvocation(configure: option => option.FunctionInvoker = pipeline.FunctionInvoker)
                .Build());
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory,
            DirectAgentChatRuntimeTests.CreateCapabilityResolver(new WorkspaceToolService(new(), new(), new())));
        var now = DateTimeOffset.UtcNow;
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id,
            new WorkspaceRoot(Guid.NewGuid(), "Workspace", _root, now, now));
        request = request with { ToolPermissionMode = scenario == "denied-write" ? ToolPermissionMode.RequireApproval : ToolPermissionMode.FullAccess };
        using var context = new SubagentActivityTestContext(rootPath: _root);
        await context.Tasks.InitializeAsync();
        var parent = new ConversationRecord(request.ConversationId, "Tool pipeline", null, ConversationMode.Programming,
            request.ToolPermissionMode, "build", now, now);
        await context.Conversations.UpsertConversationAsync(parent);
        using var state = new ConversationRuntimeState(parent, [], []);
        var turn = new AgentTurnState(request.TurnId, request.Agent);
        var committer = new DesktopTurnFinalizer(context.Conversations, NullLogger<DesktopTurnFinalizer>.Instance);
        context.Recorder.BeginTurn(state, turn);
        var completed = new List<ToolCallCompletedEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            await context.Recorder.ApplyEventAsync(state, turn, item, committer, CancellationToken.None);
            if (item is ToolCallCompletedEvent result) completed.Add(result);
        }

        completed.Should().ContainSingle().Which.Status.Should().Be(expectedStatus);
        var contract = provider.ToolResult.Should().BeOfType<DirectToolResult>().Which;
        contract.Status.Should().Be(expectedStatus);
        var modelJson = JsonSerializer.SerializeToElement(contract);
        modelJson.TryGetProperty("Content", out _).Should().BeTrue();
        modelJson.TryGetProperty("Detail", out _).Should().BeFalse("display text must not duplicate the model payload");
        (await context.Conversations.ListToolExecutionsAsync(parent.Id)).Should().ContainSingle().Which.Status.Should().Be(persistedStatus);
        File.Exists(Path.Combine(_root, "output.txt")).Should().Be(scenario == "write");
    }

    [Fact]
    public void Binding_rejects_duplicates_and_never_creates_descriptors_for_absent_tools()
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid());
        var function = AIFunctionFactory.Create(() => "ok", "same");
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("same", ToolCallKind.Read));
        Action duplicate = () => DirectTurnCapabilityResolver.BindTools(request, [binding, binding]);
        duplicate.Should().Throw<InvalidDataException>().WithMessage("*collision*");
        DirectTurnCapabilityResolver.BindTools(request, []).Should().BeEmpty();
    }

    [Fact]
    public void Binding_returns_the_bound_tool_unwrapped()
    {
        // Approval and the execution checkpoint belong to the single FunctionInvoker seam, so the
        // resolver must hand the runtime the same tool instance it was given.
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid());
        var function = AIFunctionFactory.Create(() => "ok", "same");
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("same", ToolCallKind.Read));

        var bound = DirectTurnCapabilityResolver.BindTools(request, [binding]).Should().ContainSingle().Subject;

        bound.Should().BeSameAs(binding);
        bound.Tool.Should().BeSameAs(function);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
