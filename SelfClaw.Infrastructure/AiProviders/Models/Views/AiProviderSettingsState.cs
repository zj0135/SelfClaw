namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record AiProviderSettingsState(
    IReadOnlyList<AiProviderView> Providers,
    Guid? DefaultModelProfileId);
