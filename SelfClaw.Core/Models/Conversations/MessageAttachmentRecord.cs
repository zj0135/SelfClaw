namespace SelfClaw.Core.Models;

public sealed record MessageAttachmentRecord(
    Guid Id,
    Guid MessageId,
    MessageAttachmentKind Kind,
    string FileName,
    string MediaType,
    string StoragePath,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);
