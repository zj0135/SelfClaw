using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Tools.Transcript;

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
        var message = new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Completed,
            now,
            now);
        var toolRun = new ToolExecutionRecord(
            Guid.NewGuid(),
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
            AfterSegmentIndex: 0,
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
        var message = new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Completed,
            now,
            now);
        var toolRun = new ToolExecutionRecord(
            Guid.NewGuid(),
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
            AfterSegmentIndex: 0,
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
        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
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
                MessageRole.User,
                "**question**",
                MessageStatus.Completed,
                now,
                now,
                Attachments: [attachment]),
            new MessageRecord(
                assistantMessageId,
                conversationId,
                MessageRole.Assistant,
                "answer",
                MessageStatus.Completed,
                now.AddSeconds(1),
                now.AddSeconds(1))
        };
        var toolRun = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "read_file",
            "{\"relativePath\":\"README.md\"}",
            ToolExecutionStatus.Completed,
            "Read README.md",
            null,
            12,
            now.AddMilliseconds(500),
            now.AddMilliseconds(600),
            MessageId: assistantMessageId,
            AfterSegmentIndex: 0);
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
                "ErrorMessage");
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

    private static TranscriptProjection CreateProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "SelfClawProjectionTests");
        return new TranscriptProjection(
            new StoragePaths(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets")));
    }

    private static TranscriptProjectionRequest CreateRequest(
        IReadOnlyList<MessageRecord>? messages = null,
        IReadOnlyList<ToolExecutionRecord>? toolRuns = null,
        IReadOnlyList<ConversationRecord>? conversations = null,
        IReadOnlyList<WorkspaceRoot>? workspaceRoots = null,
        Guid? selectedConversationId = null)
        => new(
            messages ?? [],
            toolRuns ?? [],
            new Dictionary<Guid, ToolRunAnchor>(),
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
            "requireApproval");
}
