namespace SelfClaw.Core.Models;

public sealed record AttachmentReference(
    Guid AttachmentId,
    string FileName,
    string MediaType,
    long ByteLength,
    string RelativePath);
