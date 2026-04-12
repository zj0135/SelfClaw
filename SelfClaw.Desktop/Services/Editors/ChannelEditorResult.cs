namespace SelfClaw.Desktop.Services;

public sealed record ChannelEditorResult(
    string ChannelId,
    string DisplayName,
    Guid? ProfileId,
    IReadOnlyDictionary<string, string> FieldValues);
