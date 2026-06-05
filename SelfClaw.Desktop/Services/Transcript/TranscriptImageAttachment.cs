namespace SelfClaw.Desktop.Services;

public sealed record TranscriptImageAttachment(
    string Id,
    string FileName,
    string MediaType,
    long ByteLength,
    string? SourceUrl);
