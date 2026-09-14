using SelfClaw.Desktop.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Abstractions;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ProgrammingSettingsTestFactory : IProgrammingCliDiscovery
{
    public static ProgrammingAssistantSettingsService Create(DesktopSettingsJsonStore store)
        => new(store, new ProgrammingSettingsTestFactory(), NullLogger<ProgrammingAssistantSettingsService>.Instance);
    public Task<IReadOnlyList<DetectedProgrammingCli>> ScanAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<DetectedProgrammingCli>>([]);
    public Task<CliTestResult> TestAsync(string? cliId, CancellationToken cancellationToken)
        => Task.FromResult(new CliTestResult(cliId ?? "", false, null, "No CLI in this fixture."));
}
