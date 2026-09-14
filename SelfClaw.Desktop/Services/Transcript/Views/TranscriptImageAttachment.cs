namespace SelfClaw.Desktop.Services.Transcript.Views;

public sealed record TranscriptImageAttachment(
    string Id,
    string FileName,
    string MediaType,
    long ByteLength,
    string? SourceUrl);
