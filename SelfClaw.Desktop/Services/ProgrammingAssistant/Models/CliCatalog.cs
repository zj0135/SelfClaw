namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

internal sealed record CliCatalog(IReadOnlyList<string> Models, IReadOnlyList<string> ReasoningLevels);
