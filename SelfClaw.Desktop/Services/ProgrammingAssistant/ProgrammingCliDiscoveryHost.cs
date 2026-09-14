using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed class ProgrammingCliDiscoveryHost : BackgroundService
{
    private readonly ProgrammingAssistantSettingsService _settings;
    private readonly ILogger<ProgrammingCliDiscoveryHost> _logger;

    public ProgrammingCliDiscoveryHost(ProgrammingAssistantSettingsService settings, ILogger<ProgrammingCliDiscoveryHost> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _settings.InitializeDiscoveryAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { _logger.LogWarning(exception, "Initial CLI discovery failed; settings can retry the scan."); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _settings.StopAsync(cancellationToken).ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
