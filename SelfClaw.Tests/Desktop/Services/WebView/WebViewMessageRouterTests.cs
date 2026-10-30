using SelfClaw.Tests.TestDoubles;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Agents.Definitions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Attachments;
using SelfClaw.Desktop.Services.Attachments.Abstractions;
using SelfClaw.Desktop.Services.Compression;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Questions;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Terminal;
using SelfClaw.Desktop.Services.Terminal.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Desktop.Services.Workspace.Abstractions;
using SelfClaw.Desktop.ViewModels;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.WebView;

public sealed class WebViewMessageRouterTests
{
    [Theory]
    [InlineData("delete-conversation", "not-a-guid", "invalid-request")]
    [InlineData("select-conversation", "not-a-guid", "invalid-request")]
    [InlineData("rename-conversation", "not-a-guid", "invalid-request")]
    [InlineData("select-conversation", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "operation-failed")]
    public async Task Conversation_command_failures_are_correlated_instead_of_reporting_success(string type, string id, string errorCode)
    {
        using var context = new RouterTestContext();
        await context.RouteAsync($$"""{"type":"{{type}}","requestId":"failed-operation","conversationId":"{{id}}"}""");
        var response = context.PostedJson.Select(json => JsonDocument.Parse(json).RootElement)
            .Single(value => value.TryGetProperty("requestId", out var request) && request.GetString() == "failed-operation");
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("errorCode").GetString().Should().Be(errorCode);
        response.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RouteAsync_routes_direct_compression_messages_to_the_bridge()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"direct-compression/get","requestId":"compression-1"}""");

        command.Should().BeNull();
        context.PostedJson.Should().ContainSingle();
        using var response = JsonDocument.Parse(context.PostedJson[0]);
        response.RootElement.GetProperty("type").GetString().Should().Be("direct-compression/state");
        response.RootElement.GetProperty("requestId").GetString().Should().Be("compression-1");
        response.RootElement.GetProperty("policy").TryGetProperty("thresholdRatio", out _).Should().BeTrue();
    }

    private const string ApplicationOrigin = "https://appassets.selfclaw.local/TranscriptVue/index.html";
    private const string ForeignOrigin = "https://foreign.example/index.html";

    [Fact]
    public async Task RouteAsync_posts_one_correlated_bridge_response()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"get-programming-assistant-settings","requestId":"request-1"}""");

        command.Should().BeNull();
        context.PostedJson.Should().ContainSingle();
        using var response = JsonDocument.Parse(context.PostedJson[0]);
        response.RootElement.GetProperty("type").GetString().Should().Be("programming-assistant-settings");
        response.RootElement.GetProperty("requestId").GetString().Should().Be("request-1");
    }

    // Every type below acts on the user's behalf, so identity has to be the sending frame's own origin —
    // not anything in the payload. A foreign origin is dropped before the message is read.
    [Theory]
    [InlineData("""{"type":"window-minimize-to-tray"}""")]
    [InlineData("""{"type":"send-prompt","prompt":"exfiltrate"}""")]
    [InlineData("""{"type":"extensions/delete","kind":"plugin","id":"rival"}""")]
    [InlineData("""{"type":"delete-workspace-root","workspaceRootId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"type":"open-link","href":"https://example.com"}""")]
    [InlineData("""{"type":"activity-panel/subscribe","subscriptionId":"11111111-1111-4111-8111-111111111111"}""")]
    [InlineData("""{"type":"activity-panel/cancel-task","subscriptionId":"11111111-1111-4111-8111-111111111111","taskId":"22222222-2222-4222-8222-222222222222"}""")]
    [InlineData("""{"type":"get-programming-assistant-settings","requestId":"request-1"}""")]
    [InlineData("""{"type":"direct-compression/save","requestId":"request-1","policy":{"enabled":false}}""")]
    [InlineData("""{"type":"attachments/pick","requestId":"attachments-1","conversationId":"11111111-1111-4111-8111-111111111111"}""")]
    public async Task RouteAsync_drops_messages_that_do_not_come_from_the_application_origin(string messageJson)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(messageJson, ForeignOrigin);

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
        context.ConversationRepository.DeletedWorkspaceRootIds.Should().BeEmpty();
        context.AgentRuntime.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://appassets.selfclaw.local/index.html")]
    [InlineData("https://appassets.selfclaw.local.evil.example/index.html")]
    [InlineData("https://evil.appassets.selfclaw.local/index.html")]
    public async Task RouteAsync_drops_messages_from_a_missing_or_lookalike_origin(string? sourceUri)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync("""{"type":"window-minimize-to-tray"}""", sourceUri);

        command.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_drops_conversation_input_actions_from_a_foreign_origin()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            $$"""{"type":"conversation-input/promote","requestId":"r1","inputId":"{{Guid.NewGuid():D}}","expectedRevision":1,"targetTurnId":"{{Guid.NewGuid():D}}"}""",
            ForeignOrigin);

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
    }

    [Fact]
    public async Task RouteAsync_dispatches_shell_intent_to_the_view_model()
    {
        using var context = new RouterTestContext();
        var workspaceRootId = Guid.NewGuid();

        await context.RouteAsync(
            $$"""{"type":"delete-workspace-root","workspaceRootId":"{{workspaceRootId:D}}"}""");

        context.ConversationRepository.DeletedWorkspaceRootIds.Should().Equal(workspaceRootId);
    }

    [Fact]
    public async Task RouteAsync_renames_a_conversation_and_reports_success()
    {
        var conversation = new ConversationRecord(Guid.NewGuid(), "旧标题", null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, DesktopAgentDefinitionService.BuildAgentId,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var context = new RouterTestContext(conversations: [conversation]);

        await context.RouteAsync(
            $$"""{"type":"rename-conversation","requestId":"rename-1","conversationId":"{{conversation.Id:D}}","title":"  新标题  "}""");

        context.ConversationRepository.UpsertedConversations.Should().ContainSingle().Which.Title.Should().Be("新标题");
        var response = context.PostedJson.Select(json => JsonDocument.Parse(json).RootElement)
            .Single(value => value.TryGetProperty("requestId", out var request) && request.GetString() == "rename-1");
        response.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RouteAsync_returns_window_command_to_the_host()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            """{"type":"open-link","href":"https://example.com/docs"}""");

        command.Should().Be(new WebViewHostCommand(
            WebViewHostCommandKind.OpenLink,
            "https://example.com/docs"));
        context.PostedJson.Should().BeEmpty();
    }

    // 关闭按钮是「收起到托盘」而不是退出：宿主只 Hide() 窗口，进程与会话继续活着。
    [Fact]
    public async Task RouteAsync_maps_the_close_button_to_minimize_to_tray()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync("""{"type":"window-minimize-to-tray"}""");

        command.Should().Be(new WebViewHostCommand(WebViewHostCommandKind.MinimizeToTray));
        context.PostedJson.Should().BeEmpty();
    }

    [Fact]
    public async Task Window_commands_are_silently_ignored_while_shutdown_is_draining()
    {
        using var context = new RouterTestContext();
        await context.Router.StopAsync(CancellationToken.None);

        // The window is hidden the moment shutdown starts, so a click on a title bar button can still
        // land here. It is not a failed operation and must not surface as one.
        var command = await context.RouteAsync("""{"type":"window-minimize-to-tray"}""");

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_window_commands_still_report_an_in_progress_shutdown()
    {
        using var context = new RouterTestContext();
        await context.Router.StopAsync(CancellationToken.None);

        await context.RouteAsync("""{"type":"select-conversation","conversationId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}""");

        context.PostedJson.Should().ContainSingle().Which.Should().Contain("shutting-down");
    }

    [Fact]
    public async Task RouteAsync_resolves_open_in_explorer_to_the_registered_workspace_root()
    {
        var directory = Directory.CreateTempSubdirectory("selfclaw-open-in-explorer").FullName;
        var workspaceRoot = new WorkspaceRoot(Guid.NewGuid(), "Repo", directory, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        try
        {
            using var context = new RouterTestContext([workspaceRoot]);

            var command = await context.RouteAsync(
                $$"""{"type":"open-in-explorer","requestId":"open-1","workspaceRootId":"{{workspaceRoot.Id:D}}"}""");

            command.Should().Be(new WebViewHostCommand(WebViewHostCommandKind.OpenInExplorer, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Open_in_explorer_fails_for_a_root_that_is_not_registered()
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(
            $$"""{"type":"open-in-explorer","requestId":"open-1","workspaceRootId":"{{Guid.NewGuid():D}}"}""");

        command.Should().BeNull();
        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Open_in_explorer_fails_when_the_registered_root_directory_is_gone()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "selfclaw-missing-" + Guid.NewGuid().ToString("N"));
        var workspaceRoot = new WorkspaceRoot(Guid.NewGuid(), "Repo", missingPath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var context = new RouterTestContext([workspaceRoot]);

        var command = await context.RouteAsync(
            $$"""{"type":"open-in-explorer","requestId":"open-1","workspaceRootId":"{{workspaceRoot.Id:D}}"}""");

        command.Should().BeNull();
        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Contain("工作目录已不存在");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"message\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"unknown\"}")]
    public async Task RouteAsync_ignores_malformed_and_unknown_messages(string messageJson)
    {
        using var context = new RouterTestContext();

        var command = await context.RouteAsync(messageJson);

        command.Should().BeNull();
        context.PostedJson.Should().BeEmpty();
    }

    [Fact]
    public void Extension_state_changes_are_pushed_through_the_host_channel()
    {
        using var context = new RouterTestContext();
        context.HostChannel.MarkReady();

        var revision = context.ExtensionStateChangeNotifier.Advance();

        var stateChangedJson = context.PostedJson
            .Single(json => json.Contains("\"type\":\"extensions/state-changed\"", StringComparison.Ordinal));
        using var message = JsonDocument.Parse(stateChangedJson);
        message.RootElement.GetProperty("revision").GetInt64().Should().Be(revision);
    }

    [Fact]
    public async Task Prompt_carries_its_model_selection_without_a_bridge_owned_vm_cache()
    {
        using var context = new RouterTestContext();

        await context.RouteAsync("""{"type":"ai-providers/list-enabled-models","requestId":"models-1"}""");
        await context.RouteAsync("""{"type":"send-prompt","prompt":"use selected model","modelProfileId":"33333333-3333-3333-3333-333333333333"}""");

        context.Inputs.Submissions.Should().ContainSingle().Which.ModelProfileId
            .Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public async Task Prompt_retries_forward_the_same_client_identity_and_explicit_conversation_target()
    {
        using var context = new RouterTestContext();
        const string id = "77777777-7777-4777-8777-777777777777";
        await context.RouteAsync($$"""{"type":"send-prompt","requestId":"transport-1","clientRequestId":"durable-1","conversationId":"{{id}}","newConversation":true,"prompt":"hello"}""");
        await context.RouteAsync($$"""{"type":"send-prompt","requestId":"transport-2","clientRequestId":"durable-1","conversationId":"{{id}}","newConversation":true,"prompt":"hello"}""");
        context.Inputs.Submissions.Should().HaveCount(2).And.OnlyContain(item =>
            item.ClientRequestId == "durable-1" && item.ConversationId == Guid.Parse(id));
    }

    [Fact]
    public async Task RouteAsync_routes_attachment_operations_to_the_attachment_bridge()
    {
        using var context = new RouterTestContext();

        await context.RouteAsync(
            $$"""{"type":"attachments/discard","requestId":"attachment-1","conversationId":"{{Guid.NewGuid():D}}","attachmentId":"{{Guid.NewGuid():D}}"}""");

        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("type").GetString().Should().Be("attachments/result");
        response.RootElement.GetProperty("requestId").GetString().Should().Be("attachment-1");
        response.RootElement.GetProperty("operation").GetString().Should().Be("discard");
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Prompt_forwards_attachment_references_to_the_durable_queue()
    {
        using var context = new RouterTestContext();
        var conversationId = Guid.NewGuid();
        var store = new AttachmentStore(context.StoragePaths);
        using var content = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02]);
        var attachment = await store.IngestAsync(conversationId, content, "photo.png", "image/png");

        await context.RouteAsync($$"""
            {"type":"send-prompt","requestId":"attachment-prompt","conversationId":"{{conversationId:D}}","newConversation":true,
             "prompt":"look at this","attachments":[{"attachmentId":"{{attachment.Id:D}}","fileName":"photo.png",
             "mediaType":"image/png","byteLength":{{attachment.ByteLength}}}]}
            """);

        var submission = context.Inputs.Submissions.Should().ContainSingle().Which;
        var stored = submission.Attachments.Should().ContainSingle().Which;
        stored.Id.Should().Be(attachment.Id);
        stored.MediaType.Should().Be("image/png");
        stored.StoragePath.Should().Be(attachment.StoragePath);
    }

    [Theory]
    [InlineData("image/tiff", 12)]
    [InlineData("image/png", 20 * 1024 * 1024)]
    public async Task Prompt_refuses_an_off_contract_attachment_reference_before_the_queue(
        string mediaType, long byteLength)
    {
        using var context = new RouterTestContext();
        var conversationId = Guid.NewGuid();
        var store = new AttachmentStore(context.StoragePaths);
        using var content = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02]);
        var attachment = await store.IngestAsync(conversationId, content, "photo.png", "image/png");

        await context.RouteAsync($$"""
            {"type":"send-prompt","requestId":"attachment-rejected","conversationId":"{{conversationId:D}}","newConversation":true,
             "prompt":"look at this","attachments":[{"attachmentId":"{{attachment.Id:D}}","fileName":"photo.png",
             "mediaType":"{{mediaType}}","byteLength":{{byteLength}}}]}
            """);

        context.Inputs.Submissions.Should().BeEmpty();
        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("type").GetString().Should().Be("prompt-submission");
        response.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("reason").GetString().Should().Be(ConversationInputReason.AttachmentInvalid);
    }

    [Fact]
    public async Task Prompt_refuses_an_attachment_that_is_not_on_disk()
    {
        using var context = new RouterTestContext();
        var conversationId = Guid.NewGuid();

        await context.RouteAsync($$"""
            {"type":"send-prompt","requestId":"attachment-missing","conversationId":"{{conversationId:D}}","newConversation":true,
             "prompt":"look at this","attachments":[{"attachmentId":"{{Guid.NewGuid():D}}","fileName":"photo.png",
             "mediaType":"image/png","byteLength":10}]}
            """);

        context.Inputs.Submissions.Should().BeEmpty();
        using var response = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        response.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        response.RootElement.GetProperty("error").GetString().Should().Be("图片已失效，请重新添加。");
    }

    [Fact]
    public async Task ResolveUserQuestion_rejects_a_prompt_id_that_is_not_in_the_batch()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);

        await context.RouteAsync(ResolvePayload(question,
            $$"""{"promptId":"{{Guid.NewGuid():D}}","selected":["PostgreSQL"]}"""));

        using var document = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        document.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("invalid-request");
        // A rejected payload must leave the question answerable.
        context.Questions.TryAnswer(question.RequestId, Skip(question.RequestId)).Should().BeTrue();
        (await pending).Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveUserQuestion_rejects_a_label_the_dock_never_offered()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);

        await context.RouteAsync(ResolvePayload(question,
            $$"""{"promptId":"{{question.Prompts[0].PromptId:D}}","selected":["MongoDB"]}"""));

        using var document = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("invalid-request");
        document.RootElement.GetProperty("error").GetString().Should().Contain("MongoDB");
        context.Questions.TryAnswer(question.RequestId, Skip(question.RequestId)).Should().BeTrue();
        (await pending).Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveUserQuestion_rejects_a_skip_that_also_carries_answers()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);

        await context.RouteAsync($$"""
            {"type":"resolve-user-question","questionRequestId":"{{question.RequestId:D}}","skipped":true,"requestId":"resolve-skip",
             "answers":[{"promptId":"{{question.Prompts[0].PromptId:D}}","selected":["SQLite"]}]}
            """);

        using var document = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("invalid-request");
        document.RootElement.GetProperty("error").GetString().Should().Contain("skipped");
        context.Questions.TryAnswer(question.RequestId, Skip(question.RequestId)).Should().BeTrue();
        (await pending).Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveUserQuestion_maps_the_batch_and_marks_an_omitted_prompt_as_skipped()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);

        await context.RouteAsync(ResolvePayload(question,
            $$"""{"promptId":"{{question.Prompts[0].PromptId:D}}","selected":["SQLite","SQLite"],"other":"  Neon  "}"""));

        var answer = await pending;
        answer.Skipped.Should().BeFalse();
        answer.Answers.Should().HaveCount(2);
        answer.Answers[0].SelectedLabels.Should().Equal("SQLite");
        answer.Answers[0].OtherText.Should().Be("Neon");
        answer.Answers[0].Skipped.Should().BeFalse();
        answer.Answers[1].Skipped.Should().BeTrue();
        using var document = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        document.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ResolveUserQuestion_refuses_a_second_submission_for_the_same_batch()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);
        var payload = ResolvePayload(question, $$"""{"promptId":"{{question.Prompts[0].PromptId:D}}","selected":["SQLite"]}""");

        await context.RouteAsync(payload);
        await pending;
        await context.RouteAsync(payload);

        using var document = JsonDocument.Parse(context.PostedJson[^1]);
        document.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be("operation-failed");
        document.RootElement.GetProperty("error").GetString()
            .Should().Be("This question has already completed or expired.");
    }

    [Fact]
    public async Task UserQuestionState_request_answers_with_the_pending_question()
    {
        using var context = new RouterTestContext();
        var question = CreateQuestion();
        var pending = context.Questions.RequestAnswerAsync(question);

        await context.RouteAsync("""{"type":"user-question/get-state","requestId":"question-state-1"}""");

        using var document = JsonDocument.Parse(context.PostedJson.Should().ContainSingle().Which);
        var state = document.RootElement;
        state.GetProperty("type").GetString().Should().Be("user-question/state");
        state.GetProperty("requestId").GetString().Should().Be("question-state-1");
        state.GetProperty("revision").GetInt64().Should().BeGreaterThan(0);
        var prompts = state.GetProperty("question").GetProperty("prompts");
        prompts.GetArrayLength().Should().Be(2);
        prompts[0].GetProperty("options")[0].GetProperty("label").GetString().Should().Be("PostgreSQL");

        context.Questions.TryAnswer(question.RequestId, Skip(question.RequestId)).Should().BeTrue();
        await pending;
    }

    private static string ResolvePayload(UserQuestionRequest question, string answer)
        => $$"""
            {"type":"resolve-user-question","questionRequestId":"{{question.RequestId:D}}","requestId":"resolve-answer","answers":[{{answer}}]}
            """;

    private static UserQuestionAnswer Skip(Guid requestId) => new(requestId, [], Skipped: true);

    private static UserQuestionRequest CreateQuestion()
        => new(
            Guid.NewGuid(),
            [
                new UserQuestionPrompt(
                    Guid.NewGuid(),
                    "Which database should this service use?",
                    "Database",
                    [new UserQuestionOption("PostgreSQL", "Relational"), new UserQuestionOption("SQLite", "Embedded")],
                    MultiSelect: false),
                new UserQuestionPrompt(
                    Guid.NewGuid(),
                    "Enable migrations on startup?",
                    null,
                    [new UserQuestionOption("Yes", null), new UserQuestionOption("No", null)],
                    MultiSelect: false)
            ],
            Guid.NewGuid());

    private sealed class RouterTestContext : IDisposable
    {
        private readonly string _storageRoot;
        private readonly TranscriptPublisher _transcriptPublisher;
        private readonly TranscriptDelivery _transcriptDelivery;
        private readonly SelfClaw.Desktop.Services.Tools.ToolApprovalPresenter _approvalPresenter;
        private readonly SelfClaw.Desktop.Services.Questions.UserQuestionPresenter _questionPresenter;
        private readonly AgentActivityCoordinator _activityCoordinator;
        private readonly TerminalHostController _terminalHostController;
        private readonly ConversationSessionCoordinator _sessions;
        private readonly ConversationTurnEngine _turnEngine;
        private readonly ConversationRunCoordinator _runs;

        public RouterTestContext(IReadOnlyList<WorkspaceRoot>? workspaceRoots = null,
            IReadOnlyList<ConversationRecord>? conversations = null)
        {
            _storageRoot = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var storagePaths = StoragePaths = StoragePathDefaults.Create(
                _storageRoot,
                Path.Combine(_storageRoot, "selfclaw.db"),
                Path.Combine(_storageRoot, "secrets"));
            var settingsStore = new DesktopSettingsJsonStore(storagePaths);
            ConversationRepository = new RecordingConversationRepository();
            if (workspaceRoots is not null)
            {
                ConversationRepository.WorkspaceRoots.AddRange(workspaceRoots);
            }

            if (conversations is not null)
            {
                ConversationRepository.Conversations.AddRange(conversations);
            }
            var approvalHandler = new DesktopToolApprovalHandler();
            var questionHandler = new DesktopUserQuestionHandler();
            _activityCoordinator = new AgentActivityCoordinator(
                approvalHandler,
                questionHandler,
                NullLogger<AgentActivityCoordinator>.Instance);
            HostChannel = new WebViewHostChannel();
            HostChannel.Attach(PostedJson.Add);
            _transcriptDelivery = new TranscriptDelivery(HostChannel, Dispatcher.CurrentDispatcher);
            _transcriptPublisher = new TranscriptPublisher(
                new TranscriptProjection(storagePaths),
                _transcriptDelivery,
                Dispatcher.CurrentDispatcher);
            var attachmentBridge = new AttachmentBridge(
                new AttachmentStore(storagePaths),
                new NoOpImageFilePicker(),
                HostChannel,
                storagePaths,
                Dispatcher.CurrentDispatcher);
            var turns = new RecordingConversationTurnRepository(ConversationRepository);
            var inputs = new EmptyConversationInputRepository();
            _runs = new ConversationRunCoordinator(inputs, new EmptyConversationInputSteeringStore(), NullLogger<ConversationRunCoordinator>.Instance);
            _sessions = new ConversationSessionCoordinator(ConversationRepository, turns, new EmptyConversationCompactionRepository(), _runs, _transcriptPublisher);
            var programmingSettings = SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(settingsStore);
            AgentRuntime = new RecordingAgentChatRuntime();
            _turnEngine = new ConversationTurnEngine(
                turns,
                new DesktopTurnFinalizer(
                    turns,
                    NullLogger<DesktopTurnFinalizer>.Instance),
                new ConversationTurnRecorder(
                    ConversationRepository, turns, inputs,
                    NullLogger<ConversationTurnRecorder>.Instance),
                AgentRuntime,
                _sessions, _runs,
                _activityCoordinator,
                approvalHandler,
                questionHandler,
                programmingSettings,

                new NoOpCompletionNotifier(),
                NullLogger<ConversationTurnEngine>.Instance);

            var agentDefinitions = new DesktopAgentDefinitionService(storagePaths);
            ExtensionStateChangeNotifier = new RecordingExtensionStateChangeNotifier();
            var agentSettings = new AgentSettingsService(agentDefinitions, new SubagentDefinitionCatalog(storagePaths),
                Unused<IExtensionSettingsService>(), ExtensionStateChangeNotifier);
            var workspaces = new ConversationWorkspaceService(ConversationRepository);
            Inputs = new SelfClaw.Tests.TestDoubles.FakeConversationInputCoordinator();
            var viewModel = new MainWindowViewModel(
                ConversationRepository,
                _turnEngine, _runs,
                Inputs,
                _sessions,
                _activityCoordinator,
                _transcriptPublisher,
                agentSettings,
                ExtensionStateChangeNotifier,
                settingsStore,
                workspaces,
                new ConversationDeletionService(_runs, _sessions, new SelfClaw.Tests.TestDoubles.NoOpSubagentConversationLifecycle(), workspaces, ConversationRepository),
                NullLogger<MainWindowViewModel>.Instance);
            viewModel.InitializeAsync().GetAwaiter().GetResult();

            var providerSettings = new RouterAiProviderSettingsService();
            var aiProviderBridge = new AiProviderSettingsBridge(providerSettings, providerSettings);
            var extensionBridge = new ExtensionSettingsBridge(
                Unused<IExtensionSettingsService>(),
                Unused<IExtensionPackageRepository>(),
                agentDefinitions,
                ExtensionStateChangeNotifier, HostChannel, Dispatcher.CurrentDispatcher);
            var petHost = new PetHost(
                new NoOpPetSettingsRepository(),
                new NoOpPetWindowAdapter(),
                new PetPackageCatalog(NullLogger<PetPackageCatalog>.Instance),
                NullLogger<PetHost>.Instance);
            _terminalHostController = new TerminalHostController(
                Unused<ITerminalSessionFactory>(),
                HostChannel,
                Dispatcher.CurrentDispatcher);
            _approvalPresenter = new SelfClaw.Desktop.Services.Tools.ToolApprovalPresenter(approvalHandler, _activityCoordinator,
                new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance), HostChannel, Dispatcher.CurrentDispatcher);
            Questions = questionHandler;
            _questionPresenter = new SelfClaw.Desktop.Services.Questions.UserQuestionPresenter(
                questionHandler, _activityCoordinator, HostChannel, Dispatcher.CurrentDispatcher);
            Router = new WebViewMessageRouter(
                aiProviderBridge,
                extensionBridge,
                new AgentSettingsBridge(agentSettings),
                new ProgrammingAssistantSettingsBridge(programmingSettings, HostChannel, Dispatcher.CurrentDispatcher),
                new AppearanceSettingsBridge(new AppearanceSettingsService(settingsStore)),
                new DirectCompressionSettingsBridge(
                    new DirectCompressionSettingsService(settingsStore, new StubAiModelCatalog()),
                    HostChannel,
                    Dispatcher.CurrentDispatcher,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DirectCompressionSettingsBridge>.Instance),
                new WorkspaceToolsSettingsBridge(
                    new WorkspaceToolsSettingsService(settingsStore),
                    HostChannel,
                    Dispatcher.CurrentDispatcher,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceToolsSettingsBridge>.Instance),
                new PetSettingsBridge(petHost, new SelfClaw.Desktop.Services.WebView.WebViewHostChannel(), System.Windows.Threading.Dispatcher.CurrentDispatcher, Microsoft.Extensions.Logging.Abstractions.NullLogger<PetSettingsBridge>.Instance),
                new WorkspaceSelectionBridge(
                    viewModel,
                    Unused<IWorkspaceFolderPicker>(),
                    Unused<IWorkspaceToolService>()),
                _terminalHostController,
                viewModel,
                _approvalPresenter,
                _questionPresenter,
                HostChannel,
                _transcriptDelivery,
                attachmentBridge: attachmentBridge);
        }

        /// <summary>
        /// Mirrors the production caller, which always hands the router the sending document's origin.
        /// </summary>
        public Task<WebViewHostCommand?> RouteAsync(string messageJson, string? sourceUri = ApplicationOrigin)
            => Router.RouteAsync(messageJson, ownerHandle: 0, sourceUri);

        public StoragePaths StoragePaths { get; }

        public RecordingConversationRepository ConversationRepository { get; }

        public RecordingAgentChatRuntime AgentRuntime { get; }

        public RecordingExtensionStateChangeNotifier ExtensionStateChangeNotifier { get; }

        public WebViewHostChannel HostChannel { get; }

        public DesktopUserQuestionHandler Questions { get; }

        public List<string> PostedJson { get; } = [];

        public SelfClaw.Tests.TestDoubles.FakeConversationInputCoordinator Inputs { get; }

        public WebViewMessageRouter Router { get; }

        public void Dispose()
        {
            Router.Dispose();
            _approvalPresenter.Dispose();
            _questionPresenter.Dispose();
            _runs.Dispose();
            _sessions.Dispose();
            _terminalHostController.Dispose();
            _activityCoordinator.Dispose();
            _transcriptPublisher.Dispose();
            _transcriptDelivery.Dispose();
        }
    }

    private static T Unused<T>() where T : class
        => DispatchProxy.Create<T, UnusedDependencyProxy>();

    private class UnusedDependencyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
            => throw new InvalidOperationException(
                $"Unexpected call to test dependency '{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}'.");
    }

    private sealed class NoOpImageFilePicker : IImageFilePicker
    {
        public IReadOnlyList<string> Pick(nint ownerHandle, string initialDirectory) => [];
    }

    private sealed class RecordingAgentChatRuntime : IAgentChatRuntime
    {
        public List<ChatTurnRequest> Requests { get; } = [];

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
            ChatTurnRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new RunCompletedEvent(RunCompletionStatus.Succeeded, "done");
        }
    }

    private sealed class RouterAiProviderSettingsService : IAiProviderSettingsService, IAiModelCatalog
    {
        public Task<IReadOnlyList<AiModelConfiguration>> ListModelConfigurationsAsync(CancellationToken cancellationToken = default) => throw Unsupported();
        public Task<AiModelConfiguration> SaveModelConfigurationAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default) => throw Unsupported();
        public Task DeleteModelConfigurationAsync(string model, CancellationToken cancellationToken = default) => throw Unsupported();

        public static readonly Guid DefaultModelId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
            => Task.FromResult<Guid?>(DefaultModelId);

        public Task<bool> IsModelAvailableAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult(modelProfileId == DefaultModelId);

        public Task<bool> IsModelMultimodalAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<EnabledModelView>>(
                [new EnabledModelView(DefaultModelId, "Model", "model", "Provider")]);

        public Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<AiProviderView> SaveProviderAsync(
            SaveProviderCommand command,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetProviderEnabledAsync(
            Guid connectionId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(
            Guid connectionId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<ConnectivityCheckResult> CheckConnectivityAsync(
            Guid connectionId,
            Guid modelProfileId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task<AiModelView> UpsertModelAsync(
            UpsertModelCommand command,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetModelEnabledAsync(
            Guid modelProfileId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetAllModelsEnabledAsync(
            Guid connectionId,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => throw Unsupported();

        public Task SetDefaultModelAsync(
            string scope,
            Guid modelProfileId,
            CancellationToken cancellationToken = default)
            => throw Unsupported();

        private static NotSupportedException Unsupported()
            => new("This AI provider operation is not used by the router test.");
    }

    private sealed class RecordingConversationRepository : IConversationRepository, IWorkspaceRootRepository
    {
        public List<Guid> DeletedWorkspaceRootIds { get; } = [];

        public List<WorkspaceRoot> WorkspaceRoots { get; } = [];

        public List<ConversationRecord> Conversations { get; } = [];

        public List<ConversationRecord> UpsertedConversations { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>(Conversations.ToArray());

        public Task<ConversationRecord?> GetConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Conversations.FirstOrDefault(item => item.Id == conversationId));

        public Task<ConversationRecord> UpsertConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken = default)
        {
            UpsertedConversations.Add(conversation);
            var index = Conversations.FindIndex(item => item.Id == conversation.Id);
            if (index >= 0) Conversations[index] = conversation;
            else Conversations.Add(conversation);
            return Task.FromResult(conversation);
        }

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>(WorkspaceRoots.ToArray());

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(
            WorkspaceRoot workspaceRoot,
            CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
        {
            DeletedWorkspaceRootIds.Add(workspaceRootId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExtensionStateChangeNotifier : IExtensionStateChangeNotifier
    {
        public long CurrentRevision { get; private set; }

        public event Action<long>? StateChanged;

        public long Advance() => AdvanceTo(CurrentRevision + 1);

        public long AdvanceTo(long revision)
        {
            if (revision <= CurrentRevision)
            {
                return CurrentRevision;
            }

            CurrentRevision = revision;
            StateChanged?.Invoke(revision);
            return revision;
        }
    }

    private sealed class NoOpCompletionNotifier : IConversationCompletionNotifier
    {
        public void Notify(ConversationRecord conversation, ConversationTurnRecord turn, IReadOnlyList<MessageRecord> messages)
        {
        }
    }

    private sealed class NoOpPetSettingsRepository : IPetSettingsRepository
    {
        public Task<PetSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PetSettings());

        public Task SaveAsync(PetSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpPetWindowAdapter : IPetWindowAdapter
    {
        public event EventHandler<PetPlacement>? PlacementCommitted
        {
            add { }
            remove { }
        }

        public Task FlushPlacementAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> GetIsVisibleAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task ShowAsync(PetSettings settings, PetLoadedPackage package, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task HideAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReloadAsync(PetSettings settings, PetLoadedPackage package, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
