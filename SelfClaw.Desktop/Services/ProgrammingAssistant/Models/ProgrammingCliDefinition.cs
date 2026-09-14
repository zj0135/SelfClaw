using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

internal sealed record ProgrammingCliDefinition(string Id, CliAgentKind Kind, string Name, string Vendor,
    IReadOnlyList<string> Commands, IReadOnlyList<string> VersionArguments, IReadOnlyList<string> Models,
    IReadOnlyList<string> ReasoningLevels, IReadOnlyList<string>? ModelListArguments,
    Func<string?, IReadOnlyList<string>>? ParseModels, Func<string?, IReadOnlyList<string>>? ParseReasoningLevels);
