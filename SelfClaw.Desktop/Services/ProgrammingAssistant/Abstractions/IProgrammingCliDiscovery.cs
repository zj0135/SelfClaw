using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions;

internal interface IProgrammingCliDiscovery
{
    Task<IReadOnlyList<DetectedProgrammingCli>> ScanAsync(CancellationToken cancellationToken);
    Task<CliTestResult> TestAsync(string? cliId, CancellationToken cancellationToken);
}
