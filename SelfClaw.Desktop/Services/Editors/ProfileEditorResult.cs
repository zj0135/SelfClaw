namespace SelfClaw.Desktop.Services;

public sealed record ProfileEditorResult(
    Guid? ProfileId,
    string Name,
    string Endpoint,
    string Model,
    bool TemperatureEnabled,
    double Temperature,
    bool TopPEnabled,
    double TopP,
    string ApiKey);
