using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Capabilities;

public sealed class SubagentCapabilitySourceTests
{
    [Fact]
    public void Resolve_exposes_four_tools_only_to_an_eligible_parent()
    {
        var source = new SubagentCapabilitySource(new NoOpCoordinator());
        var ceiling = CreateCeiling();

        var tools = source.Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["reviewer"]), ceiling);
        var noneForUnbound = source.Resolve(CreateRequest(DirectTurnOrigin.Interactive, []), ceiling);
        var noneForChild = source.Resolve(CreateRequest(DirectTurnOrigin.Subagent, ["reviewer"]), ceiling);

        tools.Tools.Select(item => item.Tool.Name).Should().Equal(
            SubagentCapabilitySource.DelegateToolName,
            SubagentCapabilitySource.GetTaskToolName,
            SubagentCapabilitySource.CancelTaskToolName,
            SubagentCapabilitySource.RetryTaskToolName);
        tools.Instructions.Should().ContainSingle();
        noneForUnbound.Tools.Should().BeEmpty();
        noneForUnbound.Instructions.Should().BeEmpty();
        noneForChild.Tools.Should().BeEmpty();
        noneForChild.Instructions.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_requires_a_concrete_parent_model_profile()
    {
        var source = new SubagentCapabilitySource(new NoOpCoordinator());
        var request = CreateRequest(DirectTurnOrigin.Interactive, ["reviewer"]) with { ModelProfileId = null };

        Action resolve = () => source.Resolve(request, CreateCeiling());

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*concrete parent model*");
    }

    [Fact]
    public void Resolve_advertises_the_allowlist_and_marks_a_definition_without_a_usable_file()
    {
        var source = new SubagentCapabilitySource(
            new NoOpCoordinator(),
            new StubSubagentDefinitionCatalog(new SubagentCatalogEntry(
                "search", "Workspace search", "Finds code in large workspaces.", "read-only")));

        var capabilities = source.Resolve(
            CreateRequest(DirectTurnOrigin.Interactive, ["search", "missing"]),
            CreateCeiling());

        var section = capabilities.Instructions.Should().ContainSingle().Subject;
        section.Should().Contain("[SelfClaw Available Subagents]");
        section.Should().Contain("- search: Workspace search - Finds code in large workspaces. Tools: read-only.");
        section.Should().Contain("- missing: missing - no usable definition file, so it cannot start");
        section.Should().Contain(SubagentCapabilitySource.DelegateToolName);
        section.Should().Contain("does not wait for the Subagent to finish");
        section.Should().Contain($"Do not poll progress with {SubagentCapabilitySource.GetTaskToolName}");
        section.Should().Contain("the runtime resumes this conversation with the completed results");
    }

    [Fact]
    public void Delegate_tool_enumerates_the_allowlist_in_its_schema_and_description()
    {
        var capabilities = new SubagentCapabilitySource(new NoOpCoordinator())
            .Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["search", "reviewer"]), CreateCeiling());

        var delegateTool = FindTool(capabilities, SubagentCapabilitySource.DelegateToolName);
        delegateTool.Description.Should().Contain("Allowed subagentId values: search, reviewer");
        delegateTool.Description.Should().Contain("runs in the background");
        delegateTool.Description.Should().Contain("Do not poll for its result");
        delegateTool.Description.Should().Contain("the runtime resumes this conversation with the completed results");

        using var schema = JsonDocument.Parse(delegateTool.JsonSchema.GetRawText());
        schema.RootElement.GetProperty("properties").GetProperty("subagentId").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).Should().Equal("search", "reviewer");
    }

    [Fact]
    public void Get_tool_restricts_itself_to_decision_bearing_reads()
    {
        var capabilities = new SubagentCapabilitySource(new NoOpCoordinator())
            .Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["search"]), CreateCeiling());

        var getTool = FindTool(capabilities, SubagentCapabilitySource.GetTaskToolName);

        getTool.Description.Should().Contain("never poll a running task with it");
        getTool.Description.Should().Contain("before a retry");
    }

    [Fact]
    public async Task Delegate_rejects_an_id_outside_the_allowlist_without_starting_a_task()
    {
        var coordinator = new RecordingCoordinator();
        var capabilities = new SubagentCapabilitySource(coordinator)
            .Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["search"]), CreateCeiling());

        var result = await FindTool(capabilities, SubagentCapabilitySource.DelegateToolName).InvokeAsync(
            new AIFunctionArguments { ["subagentId"] = "explore", ["task"] = "look around" });

        var failure = result.Should().BeOfType<DirectToolResult>().Subject;
        failure.Status.Should().Be(ToolCallStatus.Failed);
        failure.Summary.Should().Contain("is not authorized").And.Contain("Available subagentId values: search");
        failure.Content.GetString().Should().Be(failure.Summary);
        coordinator.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Delegate_starts_an_allowlisted_task_and_returns_its_durable_state()
    {
        var coordinator = new RecordingCoordinator();
        var capabilities = new SubagentCapabilitySource(coordinator)
            .Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["search"]), CreateCeiling());

        var result = await FindTool(capabilities, SubagentCapabilitySource.DelegateToolName).InvokeAsync(
            new AIFunctionArguments { ["subagentId"] = "search", ["task"] = "find the caller" });

        var completed = result.Should().BeOfType<DirectToolResult>().Subject;
        completed.Status.Should().Be(ToolCallStatus.Completed);
        coordinator.StartCalls.Should().Be(1);
        coordinator.LastSubagentId.Should().Be("search");
        completed.Content.GetProperty("SubagentId").GetString().Should().Be("search");
    }

    [Fact]
    public async Task Cancel_reports_a_task_of_another_conversation_as_a_recoverable_failure()
    {
        var capabilities = new SubagentCapabilitySource(new RecordingCoordinator())
            .Resolve(CreateRequest(DirectTurnOrigin.Interactive, ["search"]), CreateCeiling());

        var result = await FindTool(capabilities, SubagentCapabilitySource.CancelTaskToolName).InvokeAsync(
            new AIFunctionArguments { ["taskId"] = Guid.NewGuid().ToString() });

        var failure = result.Should().BeOfType<DirectToolResult>().Subject;
        failure.Status.Should().Be(ToolCallStatus.Failed);
        failure.Summary.Should().Contain("exists in this conversation");
    }

    private static AIFunction FindTool(SubagentCapabilities capabilities, string name)
        => capabilities.Tools.Single(item => item.Tool.Name == name).Tool;

    private static DirectChatTurnRequest CreateRequest(
        DirectTurnOrigin origin,
        IReadOnlyList<string> subagentIds)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turnId = Guid.NewGuid();
        return new DirectChatTurnRequest(
            turnId,
            conversationId,
            WorkspaceRoot: null,
            new AgentRuntimeDefinition(
                "build",
                "Build",
                string.Empty,
                AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                subagentIds,
                string.Empty),
            [new MessageRecord(
                Guid.NewGuid(),
                conversationId,
                turnId,
                1,
                MessageRole.User,
                "work",
                MessageStatus.Sealed,
                now,
                now)],
            [new ConversationTurnRecord(turnId, conversationId, AgentExecutionMode.Direct, origin, ConversationTurnStatus.Running, now)],
            Guid.NewGuid(),
            ToolPermissionMode.FullAccess,
            ToolApprovalHandler: null,
            new DirectTurnExecutionContext(origin, origin == DirectTurnOrigin.Interactive ? null : CreateCeiling(), null));
    }

    private static DirectCapabilityCeiling CreateCeiling()
        => new(AgentRuntimeDefinition.SystemToolPolicy, [], [], [], ["reviewer", "search", "missing"]);

    private static SubagentTaskView CreateView(string subagentId, SubagentTaskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentTaskView(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            subagentId,
            $"{subagentId} name",
            "task",
            status,
            1,
            RetryOfTaskId: null,
            ResolvedModelProfileId: Guid.NewGuid(),
            FinalText: null,
            InputTokens: null,
            OutputTokens: null,
            ErrorCode: null,
            ErrorMessage: null,
            QueuedAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
    }

    internal sealed class RecordingCoordinator : ISubagentTaskCoordinator
    {
        public int StartCalls { get; private set; }

        public string? LastSubagentId { get; private set; }

        public Task<SubagentTaskView> StartAsync(
            SubagentTaskStartRequest request,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            LastSubagentId = request.SubagentId;
            return Task.FromResult(CreateView(request.SubagentId, SubagentTaskStatus.Queued));
        }

        public Task<SubagentTaskView?> GetAsync(
            SubagentTaskQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SubagentTaskView?>(null);

        public Task<SubagentTaskView> CancelAsync(
            SubagentTaskCommand command,
            CancellationToken cancellationToken = default)
            => throw new KeyNotFoundException("The Subagent task was not found.");

        public Task<SubagentTaskView> RetryAsync(
            SubagentTaskRetryRequest request,
            CancellationToken cancellationToken = default)
            => throw new KeyNotFoundException("The Subagent task was not found.");
    }

    internal sealed class NoOpCoordinator : ISubagentTaskCoordinator
    {
        public Task<SubagentTaskView> StartAsync(
            SubagentTaskStartRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView?> GetAsync(
            SubagentTaskQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView> CancelAsync(
            SubagentTaskCommand command,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubagentTaskView> RetryAsync(
            SubagentTaskRetryRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
