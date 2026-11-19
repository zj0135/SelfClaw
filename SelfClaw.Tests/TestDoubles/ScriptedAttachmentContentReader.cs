using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>
/// Serves scripted images by message id without touching the disk, and records which messages a turn asked
/// about: the bytes a turn reads are part of what it costs, so a test can assert what was never requested.
/// </summary>
internal sealed class ScriptedAttachmentContentReader : IAttachmentContentReader
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

    private readonly Dictionary<Guid, IReadOnlyList<AIContent>> _images = [];

    internal List<Guid> RequestedMessageIds { get; } = [];

    internal ScriptedAttachmentContentReader WithImage(Guid messageId, string fileName)
    {
        _images[messageId] = [new DataContent(PngBytes, "image/png") { Name = fileName }];
        return this;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>> ReadAsync(
        IReadOnlyList<MessageRecord> messages,
        CancellationToken cancellationToken = default)
    {
        RequestedMessageIds.AddRange(messages.Select(message => message.Id));
        // Like the real reader: only the messages it was handed can contribute content.
        IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>> contents = messages
            .Where(message => _images.ContainsKey(message.Id))
            .ToDictionary(message => message.Id, message => _images[message.Id]);
        return Task.FromResult(contents);
    }
}
