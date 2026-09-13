namespace SelfClaw.Core.Models;

public sealed record EnabledModelView(
    Guid ModelProfileId,
    string Name,
    string Model,
    string ProviderName);
