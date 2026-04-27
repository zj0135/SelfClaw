namespace SelfClaw.Desktop.Services;

public sealed record ShellSelectOption(
    string Id,
    string Label,
    string? Description = null,
    bool? TemperatureEnabled = null,
    double? Temperature = null,
    bool? TopPEnabled = null,
    double? TopP = null,
    string? Model = null);
