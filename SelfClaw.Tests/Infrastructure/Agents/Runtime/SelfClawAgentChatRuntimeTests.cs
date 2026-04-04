using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Tests.Runtime;

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
    public async Task Team_mode_runs_workers_sequentially_across_rounds_and_shares_discussion_context()
    {
        var executionService = new FakeAgentExecutionService(autoDocumentDecision: false);
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateTeamRequest(teamMaxRounds: 2, TeamOutputMode.ReplyOnly);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        executionService.Requests.Select(item => item.Name).Should().Equal(
            "Coordinator",
            "Product Manager",
            "Architect",
            "Product Manager",
            "Architect",
            "Coordinator");

        executionService.Requests[2].Prompt.Should().Contain("PM round 1");
        executionService.Requests[2].Prompt.Should().NotContain("Architect round 1");

        executionService.Requests[3].Prompt.Should().Contain("PM round 1");
        executionService.Requests[3].Prompt.Should().Contain("Architect round 1");
        executionService.Requests[3].Prompt.Should().NotContain("PM round 2");

        executionService.Requests[4].Prompt.Should().Contain("PM round 2");
        executionService.Requests[4].Prompt.Should().Contain("Architect round 1");

        events.OfType<AssistantMessageCompletedEvent>()
            .Select(item => item.Message.AgentName)
            .Should().Equal("Product Manager", "Architect", "Product Manager", "Architect", "Coordinator");

        events.Should().NotContain(item => item is TeamDocumentReadyEvent);
    }

    [Fact]
    public async Task Team_mode_only_prepares_document_export_when_requested_by_output_mode()
    {
        var executionService = new FakeAgentExecutionService(autoDocumentDecision: false);
        var runtime = new SelfClawAgentChatRuntime(new NullWorkspaceToolService(), executionService);
        var request = CreateTeamRequest(
            teamMaxRounds: 1,
            TeamOutputMode.AlwaysDocument,
            workspaceRoot: new WorkspaceRoot(Guid.NewGuid(), "Repo", @"D:\Repositories\SelfClaw", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.OfType<TeamDocumentReadyEvent>()
            .Should().ContainSingle()
            .Which.SuggestedRelativePath.Should().StartWith("docs/selfclaw-team/");
    }

    private static ChatTurnRequest CreateTeamRequest(
        int teamMaxRounds,
        TeamOutputMode outputMode,
        WorkspaceRoot? workspaceRoot = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var profile = new ProviderProfile(
            Guid.NewGuid(),
            "Local",
            "https://api.example.com/v1",
            "gpt-test",
            ApiStyle.OpenAICompatible,
            "secret:test",
            now,
            now);

        return new ChatTurnRequest(
            conversationId,
            profile,
            "test-key",
            workspaceRoot,
            ConversationMode.Team,
            ToolPermissionMode.RequireApproval,
            teamMaxRounds,
            outputMode,
            null,
            [
                new MessageRecord(
                    Guid.NewGuid(),
                    conversationId,
                    MessageRole.User,
                    "请团队顺序讨论后给出结论。",
                    MessageStatus.Completed,
                    now,
                    now)
            ],
            []);
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

    private sealed class FakeAgentExecutionService : IAgentExecutionService
    {
        private readonly Dictionary<string, int> _agentRunCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _autoDocumentDecision;

        public FakeAgentExecutionService(bool autoDocumentDecision)
        {
            _autoDocumentDecision = autoDocumentDecision;
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
            Requests.Add(new CapturedAgentRequest(request.Name, prompt));

            var result = request.Name switch
            {
                "Coordinator" when request.Instructions.Contains("Return JSON only", StringComparison.Ordinal) =>
                    "{\"documentTitle\":\"Sequential Team Flow\",\"agents\":[{\"name\":\"Product Manager\",\"role\":\"Requirements\",\"mission\":\"Clarify the user intent and acceptance criteria.\"},{\"name\":\"Architect\",\"role\":\"Architecture\",\"mission\":\"Shape the technical design and trade-offs.\"}]}",
                "Coordinator" when request.Instructions.Contains("Decide whether the final team answer", StringComparison.Ordinal) =>
                    $"{{\"shouldExportDocument\":{(_autoDocumentDecision ? "true" : "false")}}}",
                "Coordinator" =>
                    "# Team Summary\n\nFinal coordinator answer.",
                "Product Manager" =>
                    $"PM round {NextRunCount(request.Name)}",
                "Architect" =>
                    $"Architect round {NextRunCount(request.Name)}",
                _ => throw new InvalidOperationException($"Unexpected agent '{request.Name}'.")
            };

            return Task.FromResult(new AgentExecutionResult(result, null, null, TimeSpan.FromMilliseconds(10)));
        }

        private int NextRunCount(string agentName)
        {
            var next = _agentRunCounts.TryGetValue(agentName, out var current) ? current + 1 : 1;
            _agentRunCounts[agentName] = next;
            return next;
        }
    }

    private sealed record CapturedAgentRequest(string Name, string Prompt);

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
