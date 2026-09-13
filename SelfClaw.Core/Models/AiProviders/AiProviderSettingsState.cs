namespace SelfClaw.Core.Models;

public sealed record AiProviderSettingsState(
    IReadOnlyList<AiProviderView> Providers,
    Guid? DefaultModelProfileId,
    IReadOnlyList<AiProviderProtocolOption> CustomProtocols);
