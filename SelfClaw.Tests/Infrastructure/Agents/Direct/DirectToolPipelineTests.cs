using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using System.Runtime.CompilerServices;
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
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);
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
        var started = await context.Turns.StartTurnAsync(new ConversationTurnStart(parent,
            new ConversationTurnRecord(request.TurnId, parent.Id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive,
                ConversationTurnStatus.Running, now), "user prompt", Guid.NewGuid()));
        var state = new ConversationRuntimeState(parent, [started.Turn], started.Messages, []);
        request = request with { Messages = state.Messages, Turns = state.Turns };
        var turn = new AgentTurnState(started.Turn, request.Agent);
        var committer = new DesktopTurnFinalizer(context.Turns, NullLogger<DesktopTurnFinalizer>.Instance);
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
        // Approval and the execution checkpoint belong to the single DirectToolInvoker, so the
        // resolver must hand the runtime the same tool instance it was given.
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid());
        var function = AIFunctionFactory.Create(() => "ok", "same");
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("same", ToolCallKind.Read));

        var bound = DirectTurnCapabilityResolver.BindTools(request, [binding]).Should().ContainSingle().Subject;

        bound.Should().BeSameAs(binding);
        bound.Tool.Should().BeSameAs(function);
    }

    [Fact]
    public async Task A_faulting_tool_reaches_the_model_as_a_failed_result_and_the_turn_still_ends()
    {
        var outcome = await RunToolLoopAsync(_ => throw new InvalidOperationException("tool exploded"));

        var modelResult = outcome.Provider.FirstToolResult.Should().BeOfType<DirectToolResult>().Subject;
        modelResult.Status.Should().Be(ToolCallStatus.Failed);
        modelResult.Summary.Should().Be("tool exploded");
        modelResult.Content.GetProperty("message").GetString().Should().Be("tool exploded");

        outcome.Terminal.Status.Should().Be(RunCompletionStatus.Failed);
        outcome.Terminal.ErrorMessage.Should().Contain("faulted 3 times consecutively").And.Contain("tool exploded");
        outcome.Executions.Should().Be(DirectToolInvoker.MaximumConsecutiveToolFaults + 1);
        outcome.Provider.Requests.Should().Be(DirectToolInvoker.MaximumConsecutiveToolFaults + 1);
    }

    [Fact]
    public async Task A_tool_that_fails_without_throwing_never_spends_the_fault_budget()
    {
        var outcome = await RunToolLoopAsync(_ => new DirectToolResult(
            ToolCallStatus.Failed,
            "tool failed",
            JsonSerializer.SerializeToElement(new { error = "tool failed" }),
            "tool failed"));

        outcome.Terminal.Status.Should().Be(RunCompletionStatus.Succeeded);
        outcome.Executions.Should().Be(RepeatingToolChatClient.RequestedToolCalls);
    }

    /// <summary>
    /// Drives one turn through the explicit Direct loop over a single synthetic tool, with a
    /// model that keeps requesting that tool until the turn ends.
    /// </summary>
    private static async Task<(RunCompletedEvent Terminal, RepeatingToolChatClient Provider, int Executions)> RunToolLoopAsync(
        Func<int, object?> tool)
    {
        var provider = new RepeatingToolChatClient("probe_tool", new Dictionary<string, object?>());
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(provider);

        var executions = 0;
        Func<object> invoke = () =>
        {
            executions++;
            return tool(executions)!;
        };
        var function = AIFunctionFactory.Create(invoke, new AIFunctionFactoryOptions
        {
            Name = "probe_tool",
            Description = "Synthetic tool used to observe fault handling.",
            ExcludeResultSchema = true,
            MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        });
        var binding = new DirectToolBinding(function, new DirectToolDescriptor("probe_tool", ToolCallKind.Other));
        var lease = new DirectTurnCapabilityLease([], [binding], new Dictionary<Guid, string>(), []);
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new FixedCapabilityResolver(lease));
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);

        RunCompletedEvent? terminal = null;
        await foreach (var item in runtime.StreamTurnAsync(request))
        {
            if (item is RunCompletedEvent completed)
            {
                terminal = completed;
            }
        }

        terminal.Should().NotBeNull();
        return (terminal!, provider, executions);
    }

    private sealed class FixedCapabilityResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(lease);
    }

    private sealed class RepeatingToolChatClient(string toolName, IDictionary<string, object?> arguments) : IChatClient
    {
        internal const int RequestedToolCalls = 5;

        private int _requests;

        internal int Requests => _requests;

        internal object? FirstToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            // The previous iteration's result is in the conversation this call receives, so every call
            // records the first tool result the model was shown.
            FirstToolResult ??= messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .LastOrDefault()?.Result;
            if (request <= RequestedToolCalls)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent($"call-{request}", toolName, arguments)])
                {
                    MessageId = $"call-message-{request}",
                    FinishReason = ChatFinishReason.ToolCalls
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
            {
                MessageId = "answer-message",
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
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
