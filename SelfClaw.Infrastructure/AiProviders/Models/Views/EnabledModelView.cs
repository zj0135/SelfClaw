namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record EnabledModelView(
    Guid ModelProfileId,
    string Name,
    string Model,
    string ProviderName);
