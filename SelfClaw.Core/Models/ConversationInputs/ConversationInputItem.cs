namespace SelfClaw.Core.Models;

public sealed record ConversationInputItem(
    Guid InputId,
    long Sequence,
    int Revision,
    Guid MessageId,
    string Prompt,
    IReadOnlyList<AttachmentReference>? Attachments = null)
{
    // A boundary batch is registered more than once from copies of the same items, so the
    // attachment list must compare by value; the synthesized record equality would compare the
    // list instance and turn an unchanged claim into "the batch changed".
    public bool Equals(ConversationInputItem? other)
        => other is not null
           && InputId == other.InputId
           && Sequence == other.Sequence
           && Revision == other.Revision
           && MessageId == other.MessageId
           && Prompt == other.Prompt
           && AttachmentsEqual(Attachments, other.Attachments);

    public override int GetHashCode()
        => HashCode.Combine(InputId, Sequence, Revision, MessageId, Prompt,
            Attachments is null ? 0 : Attachments.Count);

    private static bool AttachmentsEqual(IReadOnlyList<AttachmentReference>? left, IReadOnlyList<AttachmentReference>? right)
        => (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => left.SequenceEqual(right)
        };
}
