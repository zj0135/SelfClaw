using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Abstractions;

/// <summary>
/// Turns stored image attachments into provider content. It is an abstraction because the Direct runtime
/// needs the bytes before it builds a prompt, and the tests must be able to run a turn without disk I/O.
/// </summary>
internal interface IAttachmentContentReader
{
    /// <summary>
    /// Reads the images of every user message that carries one, keyed by message id. A missing or unreadable
    /// file only drops that attachment: an incomplete disk must never fail the turn.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>> ReadAsync(
        IReadOnlyList<MessageRecord> messages,
        CancellationToken cancellationToken = default);
}
