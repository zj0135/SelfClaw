using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

public sealed record DetectedProgrammingCli(
    string Id,
    CliAgentKind Kind,
    string Name,
    string Vendor,
    string Version,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ReasoningLevels);
