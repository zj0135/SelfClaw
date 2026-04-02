namespace SelfClaw.Desktop.Services;

public sealed record ProfileEditorResult(
    Guid? ProfileId,
    string Name,
    string Endpoint,
    string Model,
    string ApiKey);