using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Attachments;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.Workspace;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Attachments;

public sealed class AttachmentStoreTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] WebpBytes = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];

    private static readonly DateTimeOffset StaleWriteTimeUtc = DateTimeOffset.UtcNow.AddHours(-25);

    private readonly string _rootPath;
    private readonly StoragePaths _storagePaths;
    private readonly string _attachmentsRoot;
    private readonly AttachmentStore _store;

    public AttachmentStoreTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storagePaths = StoragePathDefaults.Create(_rootPath, Path.Combine(_rootPath, "selfclaw.db"), Path.Combine(_rootPath, "secrets"));
        _attachmentsRoot = AttachmentPathResolver.ResolveRoot(_rootPath);
        _store = new AttachmentStore(_storagePaths);
    }

    [Fact]
    public async Task Ingest_writes_png_into_the_conversation_directory()
    {
        var conversationId = Guid.NewGuid();

        var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "photo.png", "image/png");

        record.MessageId.Should().Be(Guid.Empty);
        record.Kind.Should().Be(MessageAttachmentKind.Image);
        record.FileName.Should().Be("photo.png");
        record.MediaType.Should().Be("image/png");
        record.ByteLength.Should().Be(PngBytes.Length);
        record.Id.Should().NotBeEmpty();
        record.StoragePath.Should().Be(Path.Combine(
            AttachmentPathResolver.ResolveConversationDirectory(_attachmentsRoot, conversationId),
            record.Id.ToString("D") + ".png"));
        File.ReadAllBytes(record.StoragePath).Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Ingest_writes_jpeg_and_webp_with_the_declared_extension()
    {
        var conversationId = Guid.NewGuid();

        var jpeg = await _store.IngestAsync(conversationId, new MemoryStream(JpegBytes), "photo.jpeg", "IMAGE/JPEG");
        var webp = await _store.IngestAsync(conversationId, new MemoryStream(WebpBytes), "photo.webp", "image/webp");

        jpeg.MediaType.Should().Be("image/jpeg");
        jpeg.StoragePath.Should().EndWith(".jpg");
        webp.StoragePath.Should().EndWith(".webp");
        File.Exists(jpeg.StoragePath).Should().BeTrue();
        File.Exists(webp.StoragePath).Should().BeTrue();
    }

    [Fact]
    public async Task Ingest_rejects_a_media_type_outside_the_whitelist()
    {
        var act = () => _store.IngestAsync(Guid.NewGuid(), new MemoryStream(PngBytes), "notes.txt", "text/plain");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ingest_rejects_content_that_does_not_match_the_declared_media_type()
    {
        var conversationId = Guid.NewGuid();
        var textBytes = "not really an image"u8.ToArray();

        var act = () => _store.IngestAsync(conversationId, new MemoryStream(textBytes), "fake.png", "image/png");

        await act.Should().ThrowAsync<ArgumentException>();
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_attachmentsRoot, conversationId);
        if (Directory.Exists(directory))
        {
            Directory.EnumerateFiles(directory).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Ingest_rejects_a_file_above_the_byte_limit()
    {
        var oversized = new byte[(10 * 1024 * 1024) + 1];
        PngBytes.CopyTo(oversized, 0);

        var act = () => _store.IngestAsync(Guid.NewGuid(), new MemoryStream(oversized), "photo.png", "image/png");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryResolveExistingFile_accepts_a_relative_path_inside_the_root()
    {
        var conversationId = Guid.NewGuid();
        var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "photo.png", "image/png");
        var relativePath = $"{conversationId:D}/{record.Id:D}.png";

        var resolved = AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, relativePath, out var fullPath);

        resolved.Should().BeTrue();
        fullPath.Should().Be(Path.GetFullPath(record.StoragePath));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("..\\escape.png")]
    [InlineData("{conversationId}/../../escape.png")]
    public void TryResolveExistingFile_rejects_parent_traversal(string relativePath)
    {
        var conversationId = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(_rootPath, "escape.png"), PngBytes);
        var candidate = relativePath.Replace("{conversationId}", conversationId.ToString("D"), StringComparison.Ordinal);

        var resolved = AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, candidate, out var fullPath);

        resolved.Should().BeFalse();
        fullPath.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveExistingFile_rejects_an_absolute_path()
    {
        var absoluteFile = Path.Combine(_rootPath, "escape.png");
        File.WriteAllBytes(absoluteFile, PngBytes);

        var resolved = AttachmentPathResolver.TryResolveExistingFile(_attachmentsRoot, absoluteFile, out _);

        resolved.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_removes_only_the_requested_attachment_and_is_idempotent()
    {
        var conversationId = Guid.NewGuid();
        var kept = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "kept.png", "image/png");
        var removed = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "removed.png", "image/png");

        await _store.DeleteAsync(conversationId, removed.Id);
        await _store.DeleteAsync(conversationId, removed.Id);

        File.Exists(removed.StoragePath).Should().BeFalse();
        File.Exists(kept.StoragePath).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteConversation_removes_the_directory_and_tolerates_a_missing_one()
    {
        var conversationId = Guid.NewGuid();
        var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "photo.png", "image/png");
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_attachmentsRoot, conversationId);

        await _store.DeleteConversationAsync(conversationId);
        await _store.DeleteConversationAsync(conversationId);

        Directory.Exists(directory).Should().BeFalse();
        File.Exists(record.StoragePath).Should().BeFalse();
    }

    [Fact]
    public async Task Maintenance_host_deletes_stale_directories_that_are_not_live_conversations()
    {
        var liveConversationId = Guid.NewGuid();
        var liveDirectory = CreateConversationDirectory(liveConversationId, "keep.png", StaleWriteTimeUtc);
        var staleDirectory = CreateConversationDirectory(Guid.NewGuid(), "drop.png", StaleWriteTimeUtc);
        var invalidDirectory = Path.Combine(_attachmentsRoot, "not-a-guid");
        Directory.CreateDirectory(invalidDirectory);
        Directory.SetLastWriteTimeUtc(invalidDirectory, StaleWriteTimeUtc.UtcDateTime);

        await CreateMaintenanceHost(liveConversationId).RunOnceAsync(CancellationToken.None);

        Directory.Exists(liveDirectory).Should().BeTrue("a live conversation keeps its directory");
        File.Exists(Path.Combine(liveDirectory, "keep.png")).Should().BeTrue();
        Directory.Exists(staleDirectory).Should().BeFalse();
        Directory.Exists(invalidDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Maintenance_host_keeps_recent_directories_even_without_a_conversation_row()
    {
        // A new conversation's directory exists before its row does; deleting recent directories
        // would drop images the user has already added to the composer.
        var recentDirectory = CreateConversationDirectory(Guid.NewGuid(), "fresh.png", DateTimeOffset.UtcNow);
        var recentInvalidDirectory = Path.Combine(_attachmentsRoot, "not-a-guid");
        Directory.CreateDirectory(recentInvalidDirectory);

        await CreateMaintenanceHost().RunOnceAsync(CancellationToken.None);

        Directory.Exists(recentDirectory).Should().BeTrue();
        File.Exists(Path.Combine(recentDirectory, "fresh.png")).Should().BeTrue();
        Directory.Exists(recentInvalidDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task Maintenance_host_is_a_no_op_when_the_attachments_root_is_missing()
    {
        await CreateMaintenanceHost().RunOnceAsync(CancellationToken.None);

        Directory.Exists(_attachmentsRoot).Should().BeFalse();
    }

    [Fact]
    public async Task Conversation_delete_removes_the_attachment_directory()
    {
        var conversationId = Guid.NewGuid();
        var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "photo.png", "image/png");
        var conversations = new StubConversationRepository();
        using var harness = new DeletionHarness(conversations, _store);

        await harness.Service.DeleteAsync([conversationId], false);

        conversations.Deleted.Should().Equal(conversationId);
        Directory.Exists(Path.GetDirectoryName(record.StoragePath)!).Should().BeFalse();
    }

    [Fact]
    public async Task Conversation_delete_succeeds_when_the_attachment_cleanup_fails()
    {
        var conversationId = Guid.NewGuid();
        var conversations = new StubConversationRepository();
        using var harness = new DeletionHarness(conversations, new ThrowingAttachmentStore());

        var deletion = () => harness.Service.DeleteAsync([conversationId], false);

        await deletion.Should().NotThrowAsync();
        conversations.Deleted.Should().Equal(conversationId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private string CreateConversationDirectory(Guid conversationId, string fileName, DateTimeOffset writeTimeUtc)
    {
        var directory = AttachmentPathResolver.ResolveConversationDirectory(_attachmentsRoot, conversationId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, PngBytes);
        File.SetLastWriteTimeUtc(path, writeTimeUtc.UtcDateTime);
        return directory;
    }

    private AttachmentMaintenanceHost CreateMaintenanceHost(params Guid[] conversationIds)
        => new(_storagePaths, new StubConversationRepository(conversationIds), NullLogger<AttachmentMaintenanceHost>.Instance);

    private sealed class StubConversationRepository(params Guid[] conversationIds) : IConversationRepository
    {
        public List<Guid> Deleted { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<ConversationRecord> records = conversationIds
                .Select(id => new ConversationRecord(
                    id, "conversation", null, ToolPermissionMode.RequireApproval, "desktop-default", now, now))
                .ToArray();
            return Task.FromResult(records);
        }

        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            Deleted.Add(conversationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);
    }

    private sealed class ThrowingAttachmentStore : IAttachmentStore
    {
        public Task<MessageAttachmentRecord> IngestAsync(
            Guid conversationId, Stream content, string fileName, string mediaType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ingest is not expected here");

        public Task DeleteAsync(Guid conversationId, Guid attachmentId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("discard is not expected here");

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("attachment directory is locked");
    }

    private sealed class EmptyWorkspaceRoots : IWorkspaceRootRepository
    {
        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopTranscriptSink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }

        public void PublishNow(bool autoScroll) { }
    }

    private sealed class DeletionHarness : IDisposable
    {
        private readonly ConversationRunCoordinator _runs;
        private readonly ConversationSessionCoordinator _sessions;

        public DeletionHarness(IConversationRepository conversations, IAttachmentStore attachments)
        {
            _runs = new ConversationRunCoordinator(
                new EmptyConversationInputRepository(),
                new EmptyConversationInputSteeringStore(),
                NullLogger<ConversationRunCoordinator>.Instance);
            _sessions = new ConversationSessionCoordinator(
                conversations,
                new RecordingConversationTurnRepository(),
                new EmptyConversationCompactionRepository(),
                _runs,
                new NoopTranscriptSink());
            Service = new ConversationDeletionService(
                _runs,
                _sessions,
                new NoOpSubagentConversationLifecycle(),
                new ConversationWorkspaceService(new EmptyWorkspaceRoots()),
                conversations,
                attachments: attachments,
                logger: NullLogger<ConversationDeletionService>.Instance);
        }

        public ConversationDeletionService Service { get; }

        public void Dispose()
        {
            _sessions.Dispose();
            _runs.Dispose();
        }
    }
}
