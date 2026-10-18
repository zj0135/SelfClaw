using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Attachments;

/// <summary>
/// A5: the reader is the only place that turns stored rows into provider content. A row that no longer
/// resolves only drops that attachment — an incomplete disk must never fail the turn.
/// </summary>
public sealed class AttachmentContentReaderTests : IDisposable
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    private readonly string _rootPath;
    private readonly StoragePaths _storagePaths;
    private readonly AttachmentStore _store;
    private readonly AttachmentContentReader _reader;

    public AttachmentContentReaderTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storagePaths = StoragePathDefaults.Create(_rootPath, Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        _store = new AttachmentStore(_storagePaths);
        _reader = new AttachmentContentReader(_storagePaths);
    }

    [Fact]
    public async Task Reads_a_stored_image_as_data_content_with_the_file_bytes()
    {
        var message = await IngestedMessageAsync();

        var contents = await _reader.ReadAsync([message]);

        var images = contents.Should().ContainKey(message.Id).WhoseValue;
        var image = images.Should().ContainSingle().Subject.Should().BeOfType<DataContent>().Subject;
        image.MediaType.Should().Be("image/png");
        image.Name.Should().Be("photo.png");
        image.Data.ToArray().Should().Equal(PngBytes);
    }

    [Fact]
    public async Task Skips_a_message_whose_file_no_longer_exists()
    {
        var message = await IngestedMessageAsync();
        var deleted = message.Attachments!.Single();
        File.Delete(deleted.StoragePath);

        var contents = await _reader.ReadAsync([message]);

        contents.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_a_row_that_points_outside_the_attachment_root()
    {
        var message = await IngestedMessageAsync();
        var outside = Path.Combine(_rootPath, "outside.png");
        await File.WriteAllBytesAsync(outside, PngBytes);
        var escaped = message with
        {
            Attachments = [message.Attachments!.Single() with { StoragePath = outside }]
        };

        var contents = await _reader.ReadAsync([escaped]);

        contents.Should().BeEmpty("containment is checked before a byte is read");
    }

    [Fact]
    public async Task Ignores_messages_that_are_not_user_messages()
    {
        var message = await IngestedMessageAsync();

        var contents = await _reader.ReadAsync([message with { Role = MessageRole.Assistant }]);

        contents.Should().BeEmpty();
    }

    private async Task<MessageRecord> IngestedMessageAsync()
    {
        var conversationId = Guid.NewGuid();
        var record = await _store.IngestAsync(conversationId, new MemoryStream(PngBytes), "photo.png", "image/png");
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(Guid.NewGuid(), conversationId, Guid.NewGuid(), 1, MessageRole.User,
            "what is this?", MessageStatus.Sealed, now, now, Attachments: [record]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
