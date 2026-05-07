using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime.Execution;
using SelfClaw.Infrastructure.Agents.Runtime.Orchestration;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

public sealed class SelfClawAgentChatRuntimeTests
{
    [Fact]
    public void ExtractText_wraps_only_text_reasoning_content_in_internal_thinking_markers()
    {
        var contents = new AIContent[]
        {
            new TextContent("Before "),
            new TextReasoningContent("internal"),
            new TextContent(" after")
        };

        SelfClawAgentChatRuntime.ExtractTextFromContents(contents)
            .Should().Be($"Before {AssistantMessageSegmenter.WrapThinking("internal")} after");
    }

    [Fact]
    public void ExtractText_does_not_treat_arbitrary_reason_named_content_as_thinking()
    {
        var contents = new AIContent[]
        {
            new FakeReasonContent("should stay out of thinking"),
            new TextContent("Visible")
        };

        SelfClawAgentChatRuntime.ExtractTextFromContents(contents).Should().Be("Visible");
    }

    [Fact]
    public async Task Programming_mode_with_plan_mode_emits_plan_events_before_final_reply()
    {
        var executionService = new FakePlannedProgrammingExecutionService();
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest(enablePlanMode: true);

        var events = (await CollectAsync(runtime.StreamTurnAsync(request))).ToList();

        events.OfType<ExecutionPlanDraftingStartedEvent>().Should().ContainSingle();
        events.OfType<ExecutionPlanPreparedEvent>()
            .Should().ContainSingle()
            .Which.Plan.Steps.Select(step => step.Title)
            .Should().Equal("Inspect the current request", "Execute the core work", "Prepare the final response");

        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.MarkdownContent)
            .Should().Equal(
                "Note for Inspect the current request",
                "Note for Execute the core work",
                "Note for Prepare the final response");

        events.OfType<ExecutionPlanStepStatusChangedEvent>()
            .Select(item => $"{item.StepId}:{item.Status}")
            .Should()
            .Equal(
                "inspect-request:Running",
                "inspect-request:Completed",
                "execute-core-work:Running",
                "execute-core-work:Completed",
                "prepare-final-response:Running",
                "prepare-final-response:Completed");

        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.AgentName)
            .Should().Equal("plan", "plan", "plan");
    }

    [Fact]
    public async Task Programming_mode_with_plan_mode_executes_steps_sequentially_and_shares_completed_context()
    {
        var executionService = new FakePlannedProgrammingExecutionService();
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest(enablePlanMode: true);

        _ = await CollectAsync(runtime.StreamTurnAsync(request));

        executionService.Requests.Select(item => item.Kind).Should().Equal("plan", "step", "step", "step");

        executionService.Requests[2].Prompt.Should().Contain("Note for Inspect the current request");
        executionService.Requests[2].Prompt.Should().NotContain("Note for Execute the core work");

        executionService.Requests[3].Prompt.Should().Contain("Note for Inspect the current request");
        executionService.Requests[3].Prompt.Should().Contain("Note for Execute the core work");

        executionService.Requests[3].Instructions.Should().Contain("Because this is the final step");
    }

    [Fact]
    public async Task Programming_mode_with_plan_mode_marks_failed_step_and_stops_following_steps()
    {
        var executionService = new FakePlannedProgrammingExecutionService(failStepNumber: 2);
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest(enablePlanMode: true);

        var (events, exception) = await CollectWithExceptionAsync(runtime.StreamTurnAsync(request));

        exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Step 2 failed.");

        events.OfType<ExecutionPlanStepStatusChangedEvent>()
            .Select(item => $"{item.StepId}:{item.Status}")
            .Should()
            .Equal(
                "inspect-request:Running",
                "inspect-request:Completed",
                "execute-core-work:Running",
                "execute-core-work:Failed");

        executionService.Requests.Select(item => item.Kind).Should().Equal("plan", "step", "step");
        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.Status)
            .Should().Equal(MessageStatus.Completed, MessageStatus.Failed);
    }

    [Fact]
    public async Task Programming_mode_plan_event_strips_internal_thinking_markers_from_summary_and_titles()
    {
        const string planPayload =
            "{\"summary\":\"<!--selfclaw:think:start-->Okay<!--selfclaw:think:end--> Visible summary\",\"steps\":[{\"id\":\"inspect-request\",\"title\":\"Inspect <!--selfclaw:think:start-->hidden<!--selfclaw:think:end--> request\"},{\"id\":\"execute-core-work\",\"title\":\"Execute core work\"},{\"id\":\"prepare-final-response\",\"title\":\"Prepare final response\"}]}";

        var executionService = new FakePlannedProgrammingExecutionService(planPayload: planPayload);
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest(enablePlanMode: true);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        var plan = events.OfType<ExecutionPlanPreparedEvent>().Single().Plan;
        plan.Summary.Should().Be("Visible summary");
        plan.Steps.Select(step => step.Title).Should().Equal("Inspect request", "Execute core work", "Prepare final response");
    }

    [Fact]
    public async Task Programming_mode_without_plan_mode_keeps_single_turn_behavior()
    {
        var executionService = new FakePlannedProgrammingExecutionService();
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest(enablePlanMode: false);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.Should().NotContain(item => item is ExecutionPlanPreparedEvent);
        events.Should().NotContain(item => item is ExecutionPlanStepStatusChangedEvent);
        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.AgentName)
            .Should().Equal("build");
        executionService.Requests.Select(item => item.Kind).Should().Equal("reply");
    }

    private static ChatTurnRequest CreateProgrammingRequest(
        bool enablePlanMode,
        string prompt = "Please inspect the task, do the work, and then summarize the outcome.",
        WorkspaceRoot? workspaceRoot = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var profile = new ProviderProfile(
            Guid.NewGuid(),
            "Local",
            "https://api.example.com/v1",
            "gpt-test",
            false,
            0.7,
            false,
            0.7,
            ApiStyle.OpenAICompatible,
            "secret:test",
            now,
            now);

        return new ChatTurnRequest(
            conversationId,
            profile,
            "test-key",
            workspaceRoot,
            ConversationMode.Programming,
            new AgentRuntimeDefinition(
                enablePlanMode ? "plan" : "build",
                enablePlanMode ? "plan" : "build",
                enablePlanMode ? "Plan agent" : "Build agent",
                enablePlanMode ? AgentExecutionMode.Plan : AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy,
                [],
                [],
                [],
                string.Empty),
            ToolPermissionMode.RequireApproval,
            null,
            [
                new MessageRecord(
                    Guid.NewGuid(),
                    conversationId,
                    MessageRole.User,
                    prompt,
                    MessageStatus.Completed,
                    now,
                    now)
            ],
            EnableReasoning: false);
    }

    private static async Task<IReadOnlyList<ChatRuntimeEvent>> CollectAsync(IAsyncEnumerable<ChatRuntimeEvent> events)
    {
        var list = new List<ChatRuntimeEvent>();
        await foreach (var item in events)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task<(IReadOnlyList<ChatRuntimeEvent> Events, Exception? Exception)> CollectWithExceptionAsync(
        IAsyncEnumerable<ChatRuntimeEvent> events)
    {
        var list = new List<ChatRuntimeEvent>();
        try
        {
            await foreach (var item in events)
            {
                list.Add(item);
            }

            return (list, null);
        }
        catch (Exception exception)
        {
            return (list, exception);
        }
    }

    private sealed class FakeReasonContent : AIContent
    {
        public FakeReasonContent(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    private sealed class FakePlannedProgrammingExecutionService : IAgentExecutionService
    {
        private readonly int? _failStepNumber;
        private readonly string _planPayload;
        private int _stepExecutionCount;

        public FakePlannedProgrammingExecutionService(
            int? failStepNumber = null,
            string? planPayload = null)
        {
            _failStepNumber = failStepNumber;
            _planPayload = planPayload ??
                           "{\"summary\":\"Plan the work first, then execute each task in order.\",\"steps\":[{\"id\":\"inspect-request\",\"title\":\"Inspect the current request\"},{\"id\":\"execute-core-work\",\"title\":\"Execute the core work\"},{\"id\":\"prepare-final-response\",\"title\":\"Prepare the final response\"}]}";
        }

        public List<CapturedAgentRequest> Requests { get; } = [];

        public Task<AgentExecutionResult> RunAsync(
            AgentExecutionRequest request,
            Func<string, CancellationToken, ValueTask>? onTextDelta,
            CancellationToken cancellationToken)
        {
            var prompt = string.Join(
                "\n\n",
                request.Messages.Select(message => SelfClawAgentChatRuntime.ExtractTextFromContents(message.Contents)));

            if (request.Instructions.Contains("Draft a compact execution plan before doing any work.", StringComparison.Ordinal))
            {
                Requests.Add(new CapturedAgentRequest(request.Name, request.Instructions, prompt, "plan"));
                return Task.FromResult(new AgentExecutionResult(
                    _planPayload,
                    null,
                    null,
                    TimeSpan.FromMilliseconds(10)));
            }

            if (request.Instructions.Contains("Execute only the current plan step and stream a normal assistant response for this step.", StringComparison.Ordinal))
            {
                _stepExecutionCount++;
                Requests.Add(new CapturedAgentRequest(request.Name, request.Instructions, prompt, "step"));
                if (_failStepNumber == _stepExecutionCount)
                {
                    throw new InvalidOperationException($"Step {_stepExecutionCount} failed.");
                }

                return Task.FromResult(new AgentExecutionResult(
                    $"Note for {ExtractPlanStepTitle(request.Instructions)}",
                    null,
                    null,
                    TimeSpan.FromMilliseconds(10)));
            }

            Requests.Add(new CapturedAgentRequest(request.Name, request.Instructions, prompt, "reply"));
            return Task.FromResult(new AgentExecutionResult(
                "# Final Answer\n\nHandled in a single programming turn.",
                null,
                null,
                TimeSpan.FromMilliseconds(10)));
        }

        private static string ExtractPlanStepTitle(string instructions)
        {
            const string marker = "Current plan step: ";
            var markerIndex = instructions.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return "Unknown step";
            }

            var startIndex = markerIndex + marker.Length;
            var endIndex = instructions.IndexOf('\n', startIndex);
            return endIndex < 0
                ? instructions[startIndex..].Trim()
                : instructions[startIndex..endIndex].Trim();
        }
    }

    private sealed record CapturedAgentRequest(
        string Name,
        string Instructions,
        string Prompt,
        string Kind);

    private sealed class NullWorkspaceToolService : IWorkspaceToolService
    {
        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(
            string workspaceRootPath,
            string? relativePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);

        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(
            string workspaceRootPath,
            string query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSearchHit>>([]);

        public Task<WorkspaceFileContent> ReadFileAsync(
            string workspaceRootPath,
            string relativePath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceFileWriteResult> WriteFileAsync(
            string workspaceRootPath,
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ShellCommandResult> RunShellCommandAsync(
            string workspaceRootPath,
            string command,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
