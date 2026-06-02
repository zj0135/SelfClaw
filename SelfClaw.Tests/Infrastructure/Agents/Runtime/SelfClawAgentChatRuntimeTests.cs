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
    public async Task Programming_mode_keeps_single_turn_behavior()
    {
        var executionService = new FakeProgrammingExecutionService();
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateProgrammingRequest();

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.OfType<AssistantMessageStartedEvent>().Should().ContainSingle();
        events.OfType<AssistantMessageCompletedEvent>()
            .Should().ContainSingle()
            .Which.Message.MarkdownContent.Should().Be("# Final Answer\n\nHandled in a single programming turn.");
        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.AgentName)
            .Should().Equal("build");
        executionService.Requests.Select(item => item.Kind).Should().Equal("reply");
    }

    private static ChatTurnRequest CreateProgrammingRequest(
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
                "build",
                "build",
                "Build agent",
                AgentExecutionMode.Direct,
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

    private sealed class FakeReasonContent : AIContent
    {
        public FakeReasonContent(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }

    private sealed class FakeProgrammingExecutionService : IAgentExecutionService
    {
        public List<CapturedAgentRequest> Requests { get; } = [];

        public Task<AgentExecutionResult> RunAsync(
            AgentExecutionRequest request,
            Func<string, CancellationToken, ValueTask>? onTextDelta,
            CancellationToken cancellationToken)
        {
            var prompt = string.Join(
                "\n\n",
                request.Messages.Select(message => SelfClawAgentChatRuntime.ExtractTextFromContents(message.Contents)));
            Requests.Add(new CapturedAgentRequest(request.Name, request.Instructions, prompt, "reply"));

            return Task.FromResult(new AgentExecutionResult(
                "# Final Answer\n\nHandled in a single programming turn.",
                null,
                null,
                TimeSpan.FromMilliseconds(10)));
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
