namespace SelfClaw.Desktop.Services;

public sealed record PromptImageAttachment(
    string SourcePath,
    string FileName,
    string MediaType,
    long ByteLength);
