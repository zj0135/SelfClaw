using SelfClaw.Desktop.Services.Transcript.Views;
using FluentAssertions;
using SelfClaw.Tests.TestDoubles;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptProjectionTests
{
    [Fact]
    public void Build_returns_null_when_the_projection_input_is_unchanged()
    {
        var projection = CreateProjection();
        var request = CreateRequest();

        projection.Build(request).Should().NotBeNull();

        projection.Build(request).Should().BeNull();
    }

    [Fact]
    public void Build_reprojects_conversation_and_workspace_metadata_without_timestamp_changes()
    {
        var now = DateTimeOffset.UtcNow;
        var workspaceRoot = new WorkspaceRoot(Guid.NewGuid(), "Before", "E:\\Before", now, now);
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Before",
            workspaceRoot.Id,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        var projection = CreateProjection();
        var request = CreateRequest(
            conversations: [conversation],
            workspaceRoots: [workspaceRoot],
            selectedConversationId: conversation.Id);

        projection.Build(request).Should().NotBeNull();

        var workspaceState = projection.Build(request with
        {
            WorkspaceRoots = [workspaceRoot with { Name = "After", RootPath = "E:\\After" }]
        });
        workspaceState.Should().NotBeNull();
        workspaceState!.Conversations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                WorkspaceRootName = "After",
                WorkspaceRootPath = "E:\\After"
            });

        var conversationState = projection.Build(request with
        {
            Conversations = [conversation with { Title = "After" }],
            WorkspaceRoots = [workspaceRoot with { Name = "After", RootPath = "E:\\After" }]
        });
        conversationState.Should().NotBeNull();
        conversationState!.Conversations.Should().ContainSingle()
            .Which.Title.Should().Be("After");
    }

    [Fact]
    public void Build_refreshes_the_message_cache_when_tool_details_change()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var message = new MessageRecord(
            messageId,
            conversationId,
            Guid.NewGuid(), 1,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Sealed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, "answer", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.ToolCall, null, toolRunId)
            ]);
        var toolRun = new ToolExecutionRecord(
            toolRunId,
            conversationId,
            "read_file",
            "{\"relativePath\":\"README.md\"}",
            ToolExecutionStatus.Completed,
            "Read README.md",
            null,
            12,
            now,
            now,
            MessageId: message.Id,
            ResultContent: "before");
        var projection = CreateProjection();
        var request = CreateRequest(messages: [message], toolRuns: [toolRun]);

        projection.Build(request).Should().NotBeNull();

        var state = projection.Build(request with
        {
            ToolRuns = [toolRun with { ResultContent = "after" }]
        });

        state.Should().NotBeNull();
        state!.Items.Should().ContainSingle()
            .Which.Segments.Should().Contain(segment =>
                segment.Kind == "tool" && segment.DetailText == "after");
    }

    [Fact]
    public void Build_limits_tool_details_in_the_wire_projection()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var message = new MessageRecord(
            messageId,
            conversationId,
            Guid.NewGuid(), 1,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Sealed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, "answer", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.ToolCall, null, toolRunId)
            ]);
        var toolRun = new ToolExecutionRecord(
            toolRunId,
            conversationId,
            "read_file",
            "{}",
            ToolExecutionStatus.Completed,
            "Read file",
            "call-1",
            12,
            now,
            now,
            MessageId: message.Id,
            ResultContent: new string('x', TranscriptToolResultLimiter.MaximumDisplayedCharacters + 1_000));

        var state = CreateProjection().Build(CreateRequest(messages: [message], toolRuns: [toolRun]));

        state.Should().NotBeNull();
        state!.Items.Single().Segments.Single(segment => segment.Kind == "tool").DetailText
            .Should().HaveLength(TranscriptToolResultLimiter.MaximumDisplayedCharacters)
            .And.EndWith("[SelfClaw truncated the displayed tool result at 24 KiB.]");
    }

    [Fact]
    public void Build_projects_messages_tools_attachments_and_conversations()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var workspaceRootId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var attachment = new MessageAttachmentRecord(
            Guid.NewGuid(),
            userMessageId,
            MessageAttachmentKind.Image,
            "screen.png",
            "image/png",
            "missing.png",
            42,
            now);
        var messages = new[]
        {
            new MessageRecord(
                userMessageId,
                conversationId,
                turnId, 1,
                MessageRole.User,
                "**question**",
                MessageStatus.Sealed,
                now,
                now,
                Attachments: [attachment]),
            new MessageRecord(
                assistantMessageId,
                conversationId,
                turnId, 2,
                MessageRole.Assistant,
                "answer",
                MessageStatus.Sealed,
                now.AddSeconds(1),
                now.AddSeconds(1),
                Segments:
                [
                    new MessageSegmentRecord(assistantMessageId, 0, MessageSegmentKind.Text, "answer", null),
                    new MessageSegmentRecord(assistantMessageId, 1, MessageSegmentKind.ToolCall, null, toolRunId)
                ])
        };
        var toolRun = new ToolExecutionRecord(
            toolRunId,
            conversationId,
            "read_file",
            "{\"relativePath\":\"README.md\"}",
            ToolExecutionStatus.Completed,
            "Read README.md",
            null,
            12,
            now.AddMilliseconds(500),
            now.AddMilliseconds(600),
            MessageId: assistantMessageId);
        var conversation = new ConversationRecord(
            conversationId,
            "Projection test",
            workspaceRootId,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        var workspaceRoot = new WorkspaceRoot(workspaceRootId, "SelfClaw", "E:\\SelfClaw", now, now);
        var request = CreateRequest(
            messages,
            [toolRun],
            [conversation],
            [workspaceRoot],
            conversationId);

        var state = CreateProjection().Build(request);

        state.Should().NotBeNull();
        state!.Items.Should().HaveCount(2);
        state.Items[0].Segments.Should().ContainSingle()
            .Which.Markdown.Should().Be("**question**");
        state.Items[0].Attachments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                FileName = "screen.png",
                MediaType = "image/png",
                ByteLength = 42L,
                SourceUrl = (string?)null
            });
        state.Items[1].Segments.Select(segment => segment.Kind)
            .Should().Equal("content", "tool");
        state.Items[1].Segments[1].ToolName.Should().Be("read_file");
        state.Conversations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                Title = "Projection test",
                WorkspaceRootId = workspaceRootId.ToString("D"),
                WorkspaceRootName = "SelfClaw",
                WorkspaceRootPath = "E:\\SelfClaw"
            });
        state.SelectedConversationId.Should().Be(conversationId.ToString("D"));
    }

    [Fact]
    public void Wire_records_expose_only_the_supported_fields()
    {
        typeof(TranscriptRenderItem).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(
                "Id",
                "Kind",
                "Role",
                "Status",
                "Segments",
                "IsThinking",
                "Timestamp",
                "Attachments",
                "TurnOutcome",
                "Compaction");
        typeof(TranscriptUsageSummary).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(
                "InputTokens",
                "OutputTokens",
                "ContextTokens",
                "ContextWindowTokens",
                "ContextIsEstimated");
        typeof(TranscriptConversationItem).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(
                "Id",
                "Title",
                "Timestamp",
            "WorkspaceRootId",
            "WorkspaceRootName",
            "WorkspaceRootPath",
            "GitRepositoryId",
            "GitRepositoryName",
            "GitBranchName",
            "IsManagedWorktree");
    }

    [Fact]
    public void Shared_message_projection_matches_main_blocks_and_preserves_thinking_identity_after_terminal_alignment()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var message = new MessageRecord(messageId, conversationId, Guid.NewGuid(), 1, MessageRole.Assistant, "answer", MessageStatus.Streaming, now, now,
            Segments:
            [
                new(messageId, 0, MessageSegmentKind.Text, "answer", null),
                new(messageId, 1, MessageSegmentKind.ToolCall, null, toolId),
                new(messageId, 2, MessageSegmentKind.Thinking, "analysis", null)
            ]);
        var tool = new ToolExecutionRecord(toolId, conversationId, "read_file", "{}", ToolExecutionStatus.Completed,
            "Read file", "call-1", 50, now, now, MessageId: messageId, ResultContent: "recorded content");
        var root = Path.Combine(Path.GetTempPath(), "SelfClawProjectionTests");
        var detailProjector = new TranscriptMessageProjector(StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets")));
        var main = CreateProjection().Build(CreateRequest(messages: [message], toolRuns: [tool]))
            ?? throw new InvalidOperationException("Missing main projection.");
        var detail = detailProjector.Build(message, PresentationHistory.Turn(message), [tool]);
        detail.Should().BeEquivalentTo(main.Items.Single());
        var thinking = detail.Segments.Single(segment => segment.Kind == "thinking");
        thinking.SegmentId.Should().Be($"{messageId:D}:thinking:0");
        thinking.IsPending.Should().BeTrue();
        var terminal = message with
        {
            Status = MessageStatus.Sealed,
            Segments =
            [
                new(messageId, 0, MessageSegmentKind.Thinking, "analysis", null),
                new(messageId, 1, MessageSegmentKind.Text, "final answer", null),
                new(messageId, 2, MessageSegmentKind.ToolCall, null, toolId)
            ]
        };
        var final = detailProjector.Build(terminal, PresentationHistory.Turn(terminal), [tool]);
        var finalThinking = final.Segments.Single(segment => segment.Kind == "thinking");
        finalThinking.SegmentId.Should().Be(thinking.SegmentId);
        finalThinking.IsPending.Should().BeFalse();
        final.Segments.Single(segment => segment.Kind == "tool").SegmentId.Should().Be(toolId.ToString("D"));
        final.Segments.Select(segment => segment.SegmentOrdinal).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Build_does_not_fall_back_to_unstructured_assistant_text_or_invent_tool_positions()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, MessageRole.Assistant,
            "legacy final text", MessageStatus.Sealed, now, now);
        var tool = new ToolExecutionRecord(Guid.NewGuid(), message.ConversationId, "read_file", "{}", ToolExecutionStatus.Completed,
            "read", "call-1", 20, now, now, MessageId: message.Id);
        var state = CreateProjection().Build(CreateRequest(messages: [message], toolRuns: [tool]))
            ?? throw new InvalidOperationException("Missing main projection.");
        state.Items.Single().Segments.Should().BeEmpty();
    }

    [Fact]
    public void Build_skips_whitespace_only_segments_so_consecutive_tools_stay_adjacent()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var firstToolId = Guid.NewGuid();
        var secondToolId = Guid.NewGuid();
        var message = new MessageRecord(messageId, conversationId, Guid.NewGuid(), 1, MessageRole.Assistant, "answer", MessageStatus.Sealed, now, now,
            Segments:
            [
                new(messageId, 0, MessageSegmentKind.ToolCall, null, firstToolId),
                new(messageId, 1, MessageSegmentKind.Text, "     ", null),
                new(messageId, 2, MessageSegmentKind.ToolCall, null, secondToolId),
                new(messageId, 3, MessageSegmentKind.Thinking, "\n", null)
            ]);
        var toolRuns = new[]
        {
            new ToolExecutionRecord(firstToolId, conversationId, "read_file", "{}", ToolExecutionStatus.Completed,
                "Read file", null, 20, now, now, MessageId: messageId),
            new ToolExecutionRecord(secondToolId, conversationId, "list_files", "{}", ToolExecutionStatus.Completed,
                "List files", null, 20, now, now, MessageId: messageId)
        };

        var state = CreateProjection().Build(CreateRequest(messages: [message], toolRuns: toolRuns))
            ?? throw new InvalidOperationException("Missing projection.");

        state.Items.Single().Segments.Select(segment => segment.Kind).Should().Equal("tool", "tool");
    }

    private static TranscriptProjection CreateProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "SelfClawProjectionTests");
        return new TranscriptProjection(
            StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets")));
    }

    [Fact]
    public void Build_marks_a_background_continuation_as_provisional_busy_state()
    {
        var projection = CreateProjection();
        var request = CreateRequest() with { IsBusy = true, IsContinuation = true };

        var state = projection.Build(request);

        state.Should().NotBeNull();
        state!.IsBusy.Should().BeTrue();
        state.IsContinuation.Should().BeTrue();
    }

    [Fact]
    public void Build_inserts_a_compression_divider_after_the_boundary_message()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        var user0 = TestMessage(conversationId, firstTurnId, 1, MessageRole.User, "U0", now);
        var assistant0 = TestMessage(conversationId, firstTurnId, 2, MessageRole.Assistant, "A0", now);
        var user1 = TestMessage(conversationId, secondTurnId, 3, MessageRole.User, "U1", now.AddMinutes(1));
        var assistant1 = TestMessage(conversationId, secondTurnId, 4, MessageRole.Assistant, "A1", now.AddMinutes(1));
        var compaction = new ConversationCompactionRecord(Guid.NewGuid(), conversationId, firstTurnId, 2,
            "compressed U0/A0", "test-model", SourceMessageCount: 2, SourceTokens: 4_200, SummaryTokens: 120,
            now.AddMinutes(2));

        var state = CreateProjection().Build(CreateRequest(
            messages: [user0, assistant0, user1, assistant1], compactions: [compaction]))
            ?? throw new InvalidOperationException("Missing projection.");

        state.Items.Select(item => item.Id).Should().Equal(
            user0.Id.ToString("D"), assistant0.Id.ToString("D"), $"compression:{compaction.Id:D}",
            user1.Id.ToString("D"), assistant1.Id.ToString("D"));
        var divider = state.Items[2];
        divider.Kind.Should().Be("compression");
        divider.Role.Should().Be("system");
        divider.Status.Should().Be("sealed");
        divider.Segments.Should().BeEmpty();
        divider.TurnOutcome.Should().BeNull();
        divider.Compaction.Should().BeEquivalentTo(new TranscriptCompactionNotice(compaction.Id.ToString("D"),
            MessageCount: 2, SourceTokens: 4_200, SummaryTokens: 120, Model: "test-model", Summary: "compressed U0/A0"));
    }

    [Fact]
    public void Build_orders_compactions_by_boundary_then_creation_time()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var firstTurnId = Guid.NewGuid();
        var secondTurnId = Guid.NewGuid();
        var user0 = TestMessage(conversationId, firstTurnId, 1, MessageRole.User, "U0", now);
        var assistant0 = TestMessage(conversationId, firstTurnId, 2, MessageRole.Assistant, "A0", now);
        var user1 = TestMessage(conversationId, secondTurnId, 3, MessageRole.User, "U1", now.AddMinutes(1));
        var assistant1 = TestMessage(conversationId, secondTurnId, 4, MessageRole.Assistant, "A1", now.AddMinutes(1));
        var lateAtFirstBoundary = Compaction(conversationId, firstTurnId, 1, "B", now.AddMinutes(1));
        var lateAtSecondBoundary = Compaction(conversationId, firstTurnId, 2, "A", now.AddMinutes(5));
        var earlyAtSecondBoundary = Compaction(conversationId, firstTurnId, 2, "C", now.AddMinutes(2));

        var state = CreateProjection().Build(CreateRequest(messages: [user0, assistant0, user1, assistant1],
            compactions: [lateAtSecondBoundary, lateAtFirstBoundary, earlyAtSecondBoundary]))
            ?? throw new InvalidOperationException("Missing projection.");

        state.Items.Select(item => item.Id).Should().Equal(
            user0.Id.ToString("D"), $"compression:{lateAtFirstBoundary.Id:D}", assistant0.Id.ToString("D"),
            $"compression:{earlyAtSecondBoundary.Id:D}", $"compression:{lateAtSecondBoundary.Id:D}",
            user1.Id.ToString("D"), assistant1.Id.ToString("D"));
    }

    [Fact]
    public void Build_leaves_the_projection_unchanged_without_compactions()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var user = TestMessage(conversationId, turnId, 1, MessageRole.User, "U0", now);
        var assistant = TestMessage(conversationId, turnId, 2, MessageRole.Assistant, "A0", now);

        var state = CreateProjection().Build(CreateRequest(messages: [user, assistant]))
            ?? throw new InvalidOperationException("Missing projection.");
        state.Items.Select(item => item.Kind).Should().Equal("message", "message");
        state.Items.Should().OnlyContain(item => item.Compaction == null && item.Role != "system");

        var withEmptyCollection = CreateProjection().Build(CreateRequest(messages: [user, assistant], compactions: []))
            ?? throw new InvalidOperationException("Missing projection.");
        withEmptyCollection.Items.Select(item => item.Id).Should().Equal(state.Items.Select(item => item.Id));
    }

    [Fact]
    public void Build_publishes_a_new_snapshot_when_the_compaction_set_changes()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var user = TestMessage(conversationId, turnId, 1, MessageRole.User, "U0", now);
        var assistant = TestMessage(conversationId, turnId, 2, MessageRole.Assistant, "A0", now);
        var first = Compaction(conversationId, turnId, 1, "first summary", now.AddMinutes(1));
        var second = Compaction(conversationId, turnId, 2, "second summary", now.AddMinutes(2));
        var projection = CreateProjection();
        var request = CreateRequest(messages: [user, assistant], compactions: [first]);

        projection.Build(request).Should().NotBeNull();
        projection.Build(request).Should().BeNull("the divider item is cached while the record is unchanged");

        var added = projection.Build(request with { Compactions = [first, second] });
        added.Should().NotBeNull();
        added!.Items.Count(item => item.Kind == "compression").Should().Be(2);

        var replaced = projection.Build(request with
        {
            Compactions = [first with { Summary = "rewritten summary" }, second]
        });
        replaced.Should().NotBeNull();
        replaced!.Items.Single(item => item.Id == $"compression:{first.Id:D}").Compaction!.Summary
            .Should().Be("rewritten summary");
    }

    [Fact]
    public void Build_sums_input_and_output_tokens_across_turns()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(InputTokens: 100, OutputTokens: 10, ContextTokens: 110)),
            UsageTurn(now.AddMinutes(1), new TurnUsage(InputTokens: 200, OutputTokens: 20, ContextTokens: 250))
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(300, 30, 250, null, false));
    }

    [Fact]
    public void Build_prefers_the_last_request_input_tokens_for_the_current_context()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(ContextTokens: 9_000, LastRequestInputTokens: 8_000, ContextWindowTokens: 32_000)),
            UsageTurn(now.AddMinutes(1), new TurnUsage(ContextTokens: 7_500, LastRequestInputTokens: 7_000, ContextWindowTokens: 32_000))
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(0, 0, 7_000, 32_000, false));
    }

    [Fact]
    public void Build_falls_back_to_the_context_snapshot_when_the_request_input_is_unknown()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(ContextTokens: 4_200)),
            UsageTurn(now.AddMinutes(1), new TurnUsage(InputTokens: 40, OutputTokens: 4, ContextTokens: 4_400))
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(40, 4, 4_400, null, false));
    }

    [Fact]
    public void Build_keeps_preferring_the_request_input_when_only_an_earlier_turn_measured_it()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(ContextTokens: 4_200, LastRequestInputTokens: 3_800)),
            UsageTurn(now.AddMinutes(1), new TurnUsage(ContextTokens: 4_400))
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(0, 0, 3_800, null, false));
    }

    [Fact]
    public void Build_keeps_the_context_window_unknown_when_no_turn_reports_one()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(InputTokens: 10, OutputTokens: 1, LastRequestInputTokens: 900))
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(10, 1, 900, null, false));
    }

    [Fact]
    public void Build_replaces_the_running_turns_persisted_usage_with_its_live_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var running = UsageTurn(now.AddMinutes(1), new TurnUsage(InputTokens: 100, OutputTokens: 10));
        var state = CreateProjection().Build(CreateRequest(
            turns: [UsageTurn(now, new TurnUsage(InputTokens: 50, OutputTokens: 5, LastRequestInputTokens: 900)), running],
            liveUsage: new TranscriptLiveUsage(running.Id,
                new TurnUsage(InputTokens: 130, OutputTokens: 14, LastRequestInputTokens: 1_200, ContextWindowTokens: 32_000),
                IsEstimated: false)));

        state.Should().NotBeNull();
        // 50 + 130, never 50 + 100 + 130: the live snapshot is the same accumulator one request later.
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(180, 19, 1_200, 32_000, false));
    }

    [Fact]
    public void Build_shows_a_locally_counted_context_as_estimated_and_keeps_the_persisted_totals()
    {
        var now = DateTimeOffset.UtcNow;
        var running = UsageTurn(now, new TurnUsage(InputTokens: 100, OutputTokens: 10, LastRequestInputTokens: 20_000));
        var state = CreateProjection().Build(CreateRequest(
            turns: [running],
            liveUsage: new TranscriptLiveUsage(running.Id,
                new TurnUsage(ContextTokens: 4_000, LastRequestInputTokens: 4_000), IsEstimated: true)));

        state.Should().NotBeNull();
        var usage = state!.Usage!;
        usage.ContextTokens.Should().Be(4_000, "a compaction's locally counted size wins over the earlier measurement");
        usage.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().Be(10);
        usage.ContextIsEstimated.Should().BeTrue();
    }

    [Fact]
    public void Build_counts_a_live_snapshot_whose_turn_is_not_in_the_turns_again()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(
            turns: [UsageTurn(now, new TurnUsage(InputTokens: 50, OutputTokens: 5, LastRequestInputTokens: 900))],
            liveUsage: new TranscriptLiveUsage(Guid.NewGuid(),
                new TurnUsage(InputTokens: 30, OutputTokens: 3, LastRequestInputTokens: 950), IsEstimated: false)));

        state.Should().NotBeNull();
        state!.Usage.Should().BeEquivalentTo(new TranscriptUsageSummary(80, 8, 950, null, false));
    }

    [Fact]
    public void Build_reports_no_usage_for_an_empty_conversation()
    {
        var state = CreateProjection().Build(CreateRequest());

        state.Should().NotBeNull();
        state!.Usage.Should().BeNull();
    }

    [Fact]
    public void Build_reports_no_usage_when_turns_carry_no_context_measurement()
    {
        var now = DateTimeOffset.UtcNow;
        var state = CreateProjection().Build(CreateRequest(turns:
        [
            UsageTurn(now, new TurnUsage(InputTokens: 500, OutputTokens: 50)),
            UsageTurn(now.AddMinutes(1), new TurnUsage())
        ]));

        state.Should().NotBeNull();
        state!.Usage.Should().BeNull();
    }

    private static ConversationTurnRecord UsageTurn(DateTimeOffset startedAtUtc, TurnUsage usage)
        => new(Guid.NewGuid(), Guid.NewGuid(), SelfClaw.Core.Runtime.AgentExecutionMode.Direct,
            SelfClaw.Core.Runtime.DirectTurnOrigin.Interactive, ConversationTurnStatus.Succeeded,
            startedAtUtc, startedAtUtc.AddMinutes(1), null, usage);

    private static MessageRecord TestMessage(Guid conversationId, Guid turnId, long sequence, MessageRole role,
        string text, DateTimeOffset timestamp)
    {
        var id = Guid.NewGuid();
        return new MessageRecord(id, conversationId, turnId, sequence, role, text, MessageStatus.Sealed,
            timestamp, timestamp,
            Segments: role == MessageRole.Assistant ? [new MessageSegmentRecord(id, 0, MessageSegmentKind.Text, text, null)] : null);
    }

    private static ConversationCompactionRecord Compaction(Guid conversationId, Guid turnId, long throughSequence,
        string summary, DateTimeOffset createdAtUtc)
        => new(Guid.NewGuid(), conversationId, turnId, throughSequence, summary, "test-model",
            SourceMessageCount: 2, SourceTokens: 1_000, SummaryTokens: 50, createdAtUtc);

    private static TranscriptProjectionRequest CreateRequest(
        IReadOnlyList<MessageRecord>? messages = null,
        IReadOnlyList<ToolExecutionRecord>? toolRuns = null,
        IReadOnlyList<ConversationRecord>? conversations = null,
        IReadOnlyList<WorkspaceRoot>? workspaceRoots = null,
        Guid? selectedConversationId = null,
        IReadOnlyList<ConversationCompactionRecord>? compactions = null,
        IReadOnlyList<ConversationTurnRecord>? turns = null,
        TranscriptLiveUsage? liveUsage = null)
        => new(
            messages ?? [],
            turns ?? PresentationHistory.Turns(messages ?? []),
            toolRuns ?? [],
            conversations ?? [],
            workspaceRoots ?? [],
            selectedConversationId,
            true,
            false,
            null,
            "direct",
            "build",
            "Build",
            1,
            "requireApproval",
            Compactions: compactions,
            LiveUsage: liveUsage);
}
