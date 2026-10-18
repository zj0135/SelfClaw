using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Attachments;
using SelfClaw.Desktop.Services.Attachments.Abstractions;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Attachments;

public sealed class AttachmentBridgeTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    private readonly string _rootPath;
    private readonly StoragePaths _storagePaths;
    private readonly string _attachmentsRoot;
    private readonly AttachmentStore _store;
    private readonly FakeImageFilePicker _picker;
    private readonly WebViewHostChannel _channel = new();
    private readonly List<string> _postedJson = [];

    public AttachmentBridgeTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        _attachmentsRoot = AttachmentPathResolver.ResolveRoot(_rootPath);
        _store = new AttachmentStore(_storagePaths);
        _picker = new FakeImageFilePicker();
        _channel.Attach(_postedJson.Add);
    }

    [Fact]
    public Task Pick_reports_an_empty_list_when_the_dialog_is_cancelled()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            await HandleAsync(bridge, "attachments/pick", new
            {
                type = "attachments/pick",
                requestId = "pick-1",
                conversationId = Guid.NewGuid().ToString("D")
            });

            var response = Response("pick-1");
            response.GetProperty("type").GetString().Should().Be("attachments/result");
            response.GetProperty("operation").GetString().Should().Be("pick");
            response.GetProperty("ok").GetBoolean().Should().BeTrue();
            response.GetProperty("attachments").GetArrayLength().Should().Be(0);
            _picker.Calls.Should().Be(1);
        });

    [Fact]
    public Task Pick_ingests_every_selected_file_and_returns_transcript_preview_urls()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            var conversationId = Guid.NewGuid();
            _picker.Files = [CreateSourceFile("screen.png", PngBytes), CreateSourceFile("photo.jpeg", JpegBytes)];

            await HandleAsync(bridge, "attachments/pick", new
            {
                type = "attachments/pick",
                requestId = "pick-2",
                conversationId = conversationId.ToString("D")
            });

            ((int)_picker.OwnerHandle).Should().Be(42);
            var attachments = Response("pick-2").GetProperty("attachments").EnumerateArray().ToArray();
            attachments.Should().HaveCount(2);
            attachments[0].GetProperty("fileName").GetString().Should().Be("screen.png");
            attachments[0].GetProperty("mediaType").GetString().Should().Be("image/png");
            attachments[0].GetProperty("byteLength").GetInt64().Should().Be(PngBytes.Length);
            var pngId = attachments[0].GetProperty("attachmentId").GetString();
            attachments[0].GetProperty("previewUrl").GetString().Should()
                .Be($"https://attachments.selfclaw.local/{conversationId:D}/{pngId}.png");
            var jpegId = attachments[1].GetProperty("attachmentId").GetString();
            attachments[1].GetProperty("mediaType").GetString().Should().Be("image/jpeg");
            attachments[1].GetProperty("previewUrl").GetString().Should()
                .Be($"https://attachments.selfclaw.local/{conversationId:D}/{jpegId}.jpg");
            AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, $"{conversationId:D}/{pngId}.png", out _)
                .Should().BeTrue();
            AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, $"{conversationId:D}/{jpegId}.jpg", out _)
                .Should().BeTrue();
        });

    [Fact]
    public Task Pick_keeps_only_the_first_file_when_multiple_is_false()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            _picker.Files = [CreateSourceFile("a.png", PngBytes), CreateSourceFile("b.png", PngBytes)];

            await HandleAsync(bridge, "attachments/pick", new
            {
                type = "attachments/pick",
                requestId = "pick-3",
                conversationId = Guid.NewGuid().ToString("D"),
                multiple = false
            });

            Response("pick-3").GetProperty("attachments").GetArrayLength().Should().Be(1);
        });

    [Fact]
    public Task Pick_rejects_an_invalid_conversation_id_without_opening_the_dialog()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            await HandleAsync(bridge, "attachments/pick", new
            {
                type = "attachments/pick",
                requestId = "pick-4",
                conversationId = "not-a-guid"
            });

            var response = Response("pick-4");
            response.GetProperty("ok").GetBoolean().Should().BeFalse();
            response.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
            _picker.Calls.Should().Be(0);
        });

    [Fact]
    public Task Ingest_bytes_writes_the_image_into_the_conversation_directory()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            var conversationId = Guid.NewGuid();

            await HandleAsync(bridge, "attachments/ingest-bytes", new
            {
                type = "attachments/ingest-bytes",
                requestId = "paste-1",
                conversationId = conversationId.ToString("D"),
                fileName = "pasted.png",
                mediaType = "image/png",
                dataBase64 = Convert.ToBase64String(PngBytes)
            });

            var response = Response("paste-1");
            response.GetProperty("ok").GetBoolean().Should().BeTrue();
            response.GetProperty("operation").GetString().Should().Be("ingest-bytes");
            var attachment = response.GetProperty("attachments")[0];
            attachment.GetProperty("fileName").GetString().Should().Be("pasted.png");
            attachment.GetProperty("byteLength").GetInt64().Should().Be(PngBytes.Length);
            var attachmentId = attachment.GetProperty("attachmentId").GetString();
            AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, $"{conversationId:D}/{attachmentId}.png", out var fullPath)
                .Should().BeTrue();
            File.ReadAllBytes(fullPath).Should().Equal(PngBytes);
        });

    [Fact]
    public Task Ingest_bytes_rejects_bytes_that_are_not_the_declared_image_and_writes_nothing()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            var conversationId = Guid.NewGuid();
            var textBytes = "not really an image"u8.ToArray();

            await HandleAsync(bridge, "attachments/ingest-bytes", new
            {
                type = "attachments/ingest-bytes",
                requestId = "paste-2",
                conversationId = conversationId.ToString("D"),
                fileName = "fake.png",
                mediaType = "image/png",
                dataBase64 = Convert.ToBase64String(textBytes)
            });

            var response = Response("paste-2");
            response.GetProperty("ok").GetBoolean().Should().BeFalse();
            response.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
            var directory = AttachmentPathResolver.ResolveConversationDirectory(_attachmentsRoot, conversationId);
            if (Directory.Exists(directory))
            {
                Directory.EnumerateFiles(directory).Should().BeEmpty();
            }
        });

    [Fact]
    public Task Ingest_bytes_rejects_an_oversized_payload_before_decoding_it()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            await HandleAsync(bridge, "attachments/ingest-bytes", new
            {
                type = "attachments/ingest-bytes",
                requestId = "paste-3",
                conversationId = Guid.NewGuid().ToString("D"),
                fileName = "huge.png",
                mediaType = "image/png",
                // Longer than the base64 form of 10 MiB and deliberately not valid base64 either, so this
                // only passes when the size check runs before decoding.
                dataBase64 = new string('A', (14 * 1024 * 1024) + 4)
            });

            var response = Response("paste-3");
            response.GetProperty("ok").GetBoolean().Should().BeFalse();
            response.GetProperty("error").GetString().Should().Contain("10 MB");
        });

    [Fact]
    public Task Discard_removes_the_file_and_stays_idempotent()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            var conversationId = Guid.NewGuid();
            _picker.Files = [CreateSourceFile("discard.png", PngBytes)];
            await HandleAsync(bridge, "attachments/pick", new
            {
                type = "attachments/pick",
                requestId = "pick-5",
                conversationId = conversationId.ToString("D")
            });
            var attachmentId = Response("pick-5").GetProperty("attachments")[0].GetProperty("attachmentId").GetString();
            var relativePath = $"{conversationId:D}/{attachmentId}.png";

            await HandleAsync(bridge, "attachments/discard", new
            {
                type = "attachments/discard",
                requestId = "discard-1",
                conversationId = conversationId.ToString("D"),
                attachmentId
            });
            AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, relativePath, out _).Should().BeFalse();
            Response("discard-1").GetProperty("ok").GetBoolean().Should().BeTrue();

            await HandleAsync(bridge, "attachments/discard", new
            {
                type = "attachments/discard",
                requestId = "discard-2",
                conversationId = conversationId.ToString("D"),
                attachmentId
            });
            Response("discard-2").GetProperty("ok").GetBoolean().Should().BeTrue();
        });

    [Fact]
    public Task Unsupported_attachment_operations_are_reported_instead_of_thrown()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge();
            await HandleAsync(bridge, "attachments/rename", new
            {
                type = "attachments/rename",
                requestId = "rename-1",
                conversationId = Guid.NewGuid().ToString("D")
            });

            var response = Response("rename-1");
            response.GetProperty("ok").GetBoolean().Should().BeFalse();
            response.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        });

    [Fact]
    public Task Preview_url_matches_the_transcript_projector_url()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var conversationId = Guid.NewGuid();
            var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "screen.png", "image/png");
            var now = DateTimeOffset.UtcNow;
            var turn = new ConversationTurnRecord(
                Guid.NewGuid(), conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive,
                ConversationTurnStatus.Succeeded, now, now);
            var message = new MessageRecord(
                Guid.NewGuid(), conversationId, turn.Id, 1, MessageRole.User, "look", MessageStatus.Sealed,
                now, now, Attachments: [record]);

            var projected = new TranscriptMessageProjector(_storagePaths).Build(message, turn, []);

            AttachmentPathResolver.TryCreateSourceUrl(_attachmentsRoot, record.StoragePath, out var bridgeUrl).Should().BeTrue();
            projected.Attachments.Should().ContainSingle().Which.SourceUrl.Should()
                .Be(bridgeUrl)
                .And.Be($"https://attachments.selfclaw.local/{conversationId:D}/{record.Id:D}.png");
        });

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    // The dispatcher the bridge replies through has to be the one the test body runs on, so the bridge is
    // built inside each WpfDispatcherTest body rather than in the fixture constructor.
    private AttachmentBridge CreateBridge()
        => new(_store, _picker, _channel, _storagePaths, Dispatcher.CurrentDispatcher);

    private static Task HandleAsync(AttachmentBridge bridge, string type, object payload)
        => bridge.HandleAsync(type, JsonSerializer.SerializeToElement(payload), ownerHandle: 42, CancellationToken.None);

    private JsonElement Response(string requestId)
    {
        using var document = JsonDocument.Parse(_postedJson
            .Single(json => json.Contains($"\"requestId\":\"{requestId}\"", StringComparison.Ordinal)));
        return document.RootElement.Clone();
    }

    private string CreateSourceFile(string fileName, byte[] bytes)
    {
        var path = Path.Combine(_rootPath, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class FakeImageFilePicker : IImageFilePicker
    {
        public List<string> Files { get; set; } = [];

        public int Calls { get; private set; }

        public nint OwnerHandle { get; private set; }

        public IReadOnlyList<string> Pick(nint ownerHandle, string initialDirectory)
        {
            Calls++;
            OwnerHandle = ownerHandle;
            return Files;
        }
    }
}
